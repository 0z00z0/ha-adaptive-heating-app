using System.Globalization;
using System.Reactive.Concurrency;
using System.Reflection;

using Adaptive.Engine;
using Adaptive.Settings;
using Adaptive.UI;

using AdaptiveHeating.AddOn.Driving;
using AdaptiveHeating.AddOn.Ingress;
using AdaptiveHeating.AddOn.Integration;
using AdaptiveHeating.AddOn.Logging;
using AdaptiveHeating.AddOn.Reporting;
using AdaptiveHeating.AddOn.Settings;
using AdaptiveHeating.Page;
using AdaptiveHeating.Page.Presentation;
using AdaptiveHeating.Page.Settings;
using AdaptiveHeating.Planner;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Ha;
using AdaptiveLighting.NetDaemon;
using AdaptiveLighting.Persistence;
using AdaptiveLighting.Web;
using AdaptiveLighting.Web.Services;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Localization;

using NetDaemon.Client;
using NetDaemon.Client.Extensions;
using NetDaemon.Client.Settings;
using NetDaemon.HassModel;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// A registration that cannot be satisfied fails the start rather than the first page that asks for it, and a
// scoped service taken from the root provider fails here rather than outliving the request it belongs to.
builder.Host.UseDefaultServiceProvider(options =>
{
	options.ValidateOnBuild = true;
	options.ValidateScopes = true;
});

// The one port Home Assistant's ingress reaches inside the container. The manifest names the same number and
// maps no port, so nothing outside the container can reach it.
int ingressPort = WayIn.PortFrom(builder.Configuration["AdaptiveHeating:IngressPort"]);

builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(ingressPort));

builder.Services.AddSingleton(new WayIn(ingressPort));
builder.Services.AddSingleton(TimeProvider.System);

// The key ring lives in the add-on's own persistent directory, not beside the binaries: an update replaces
// the image and everything in it, and a lost key ring logs every open page out with no way back.
string keyRing = builder.Configuration["AdaptiveHeating:KeyRingPath"] ?? "/data/keys";

Directory.CreateDirectory(keyRing);
builder.Services.AddDataProtection()
	.PersistKeysToFileSystem(new DirectoryInfo(keyRing))
	.SetApplicationName("adaptive-heating");

// The settings document and the notes beside it live in the add-on's own persistent directory, which is the
// one thing on the box an update does not replace.
string documentPath = builder.Configuration[HeatingConfigPath.ConfigPathKey]
	?? throw new InvalidOperationException($"{HeatingConfigPath.ConfigPathKey} is not set in appsettings.json.");

// The household's own clock: the one zone a boundary written as a wall time is decided on, and the one every
// time a person reads is shown in. Read once, here, and handed to everything that needs it. Above the logging
// call because the log is stamped with it, and a second read anywhere would put two clocks in one process.
TheHouseholdClock household = TheHouseholdClock.From(CoreApi.SupervisorZone(), TimeZoneInfo.Local);

builder.Services.AddSingleton(household);

// The durable copy of the log goes beside that document, because the Supervisor's own buffer holds only the
// newest lines and two restarts overwrite it. Before anything else is registered, so the first line is logged
// the same way as the last.
builder.UseIsoTimestampLogging(
	household,
	LogBesideTheDocument.Directory(documentPath),
	LogBesideTheDocument.Stem(documentPath, "adaptive-heating"));

// A stamp made before this build was written to disk cannot have been made against a clock set from the
// network, because the software did not exist then.
NetworkSetClock stamps = new(TimeProvider.System, NetworkSetClock.BuiltAt(Assembly.GetExecutingAssembly()));

builder.Services.AddSingleton(stamps);
builder.Services.AddSingleton<IStampSource>(stamps);

builder.Services.AddSingleton(services =>
	new StateStoreRegistry(
		documentPath,
		services.GetRequiredService<ILogger<StateStoreRegistry>>(),
		services.GetRequiredService<IScheduler>()));

builder.Services.AddSingleton(services =>
{
	DurableSettingsStore durable = DurableSettingsStore.Open(
		services.GetRequiredService<StateStoreRegistry>(),
		services.GetRequiredService<TimeProvider>(),
		// Nothing has been heard from the integration and no file was found, so the page opens on no rooms
		// and says so rather than showing an empty table as though it were a finished one.
		HeatingDocument.NothingHeardYet(),
		services.GetRequiredService<ILogger<DurableSettingsStore>>(),
		RoomTemperatures.FromTheDocument(
			() => services.GetRequiredService<HeatingDocumentHost>().Current,
			// Its own catalog: the page's is scoped to a circuit, and this store outlives every circuit.
			new HaCatalog(
				() => services.GetRequiredService<TheHouseAsItIs>().Context,
				() => services.GetRequiredService<TheHouseAsItIs>().Registry,
				services.GetRequiredService<ILoggerFactory>()),
			() =>
			{
				// The option actually in force, which an overlay entity can set without the select moving at all.
				HouseState house = services.GetRequiredService<TheHouseMode>().Now().State;

				return house.Forced?.OptionValue is { Length: > 0 } forced ? forced : house.ModeValue;
			},
			household.Zone,
			services.GetRequiredService<TimeProvider>()));

	stamps.ClockWasSet += durable.WhenTheClockIsSet;

	return durable;
});

// The Supervisor proxies Home Assistant's websocket on the token in the add-on's environment. No user and no
// long-lived token exist anywhere. One connection for the life of the process, held by NetDaemon's own client,
// and everything the add-on asks of Home Assistant goes over it.
builder.Services.AddHomeAssistantClient();

// The client's HTTP half is never called, and it still reads these to build an address and a header at
// construction. Naming the Supervisor keeps it from pointing at a Home Assistant nobody asked for.
builder.Services.Configure<HomeAssistantSettings>(settings =>
{
	settings.Host = CoreConnection.SupervisorHost;
	settings.Port = CoreConnection.SupervisorPort;
	settings.Ssl = false;
	settings.WebsocketPath = CoreConnection.SupervisorWebsocketPath;
	settings.Token = CoreApi.SupervisorToken() ?? string.Empty;
});

// The boundary timer schedules on this. The rest of the add-on keeps its own clock, which is what a settings
// stamp and a warm-up deadline are measured against.
builder.Services.AddSingleton<IScheduler>(Scheduler.Default);

// The document's own file, resolved once and server-side, so no request can influence what is written.
builder.Services.AddSingleton(services =>
	HeatingConfigPath.Resolve(
		builder.Configuration,
		builder.Environment.ContentRootPath,
		services.GetRequiredService<ILogger<HeatingConfigStore>>()));

builder.Services.AddSingleton(services =>
	new HeatingConfigStore(
		services.GetRequiredService<ConfigLocation>().Path,
		services.GetRequiredService<ILogger<HeatingConfigStore>>()));

// The document's LogLevel moves the level the logger above was built on, while the add-on runs.
builder.Services.AddSingleton<ILogLevelControl, SerilogLevelControl>();

// The one validated write path over that file: normalise, validate, refuse or write, read back, then ask for a
// pass. Every writer comes through it, and it carries the stamp check that refuses a write landing on a file
// another writer has moved on since.
builder.Services.AddSingleton(services =>
	new HeatingDocumentHost(
		services.GetRequiredService<HeatingConfigStore>(),
		document => WhatTheDocumentNames.CheckedAgainst(services.GetRequiredService<TheHouseAsItIs>(), document),
		() => services.GetRequiredService<ConnectionToTheIntegration>().OnePassPlease(),
		services.GetRequiredService<ILogger<HeatingDocumentHost>>(),
		services.GetRequiredService<ILogLevelControl>()));

// What the heating reads beyond the climate entities and the sun, read from the document each time, so a
// changed entity choice takes effect without a restart.
builder.Services.AddSingleton(services =>
	new TheHouseAsItIs(() =>
		WhatTheDocumentNames.Entities(services.GetRequiredService<HeatingDocumentHost>().Current.Global)));
// What the settings page's pickers offer, read from the add-on's own model of the house each time it is asked.
// A circuit's own IHaContext would have empty caches: only the model brought up per connection is filled.
builder.Services.AddScoped(services =>
{
	TheHouseAsItIs house = services.GetRequiredService<TheHouseAsItIs>();
	TheAreasOfTheHouse areas = services.GetRequiredService<TheAreasOfTheHouse>();

	return new HaCatalog(() => house.Context, () => house.Registry, services.GetRequiredService<ILoggerFactory>(), areas, areas);
});

// Lighting's mode service: the house card sets the house mode, and the House tab flips the master switch.
builder.Services.AddScoped(services =>
{
	TheHouseAsItIs house = services.GetRequiredService<TheHouseAsItIs>();

	return new ModeService(
		() => house.Context,
		services.GetRequiredService<HaCatalog>(),
		services.GetRequiredService<HeatingDocumentHost>(),
		services.GetRequiredService<ILogger<ModeService>>());
});
builder.Services.AddSingleton<WhatIsHidden>();

// The Areas as the engine asks them, and the two entity facts the area registry does not carry. The settings
// page reads this to group the rooms by floor, so it is registered under the interface as well.
builder.Services.AddSingleton<TheAreasOfTheHouse>();
builder.Services.AddSingleton<IAreaRegistry>(services => services.GetRequiredService<TheAreasOfTheHouse>());

builder.Services.AddSingleton(services =>
	new AreaDiscoveryScheduler(
		services.GetRequiredService<HeatingDocumentHost>(),
		services.GetRequiredService<ILogger<AreaDiscoveryScheduler>>()));

builder.Services.AddSingleton(services =>
	new CoreConnection(
		services.GetRequiredService<IHomeAssistantRunner>(),
		CoreApi.SupervisorToken,
		services.GetRequiredService<TheHouseAsItIs>(),
		services.GetRequiredService<TimeProvider>(),
		services.GetRequiredService<ILogger<CoreConnection>>()));

builder.Services.AddHostedService(services => services.GetRequiredService<CoreConnection>());

// NetDaemon's entity model over that same connection: the state cache, the five registries and the typed
// entities. Its runtime is not referenced, so the caches are filled here instead, once per connection.
builder.Services.AddScopedHaContext();

builder.Services.AddHostedService(services =>
	new StartsTheEntityModel(
		services.GetRequiredService<CoreConnection>(),
		services.GetRequiredService<TheHouseAsItIs>(),
		services.GetRequiredService<WhatIsHidden>(),
		() => WhatTheDocumentNames.Entities(services.GetRequiredService<HeatingDocumentHost>().Current.Global),
		services.GetRequiredService<IServiceScopeFactory>(),
		services.GetRequiredService<ILogger<StartsTheEntityModel>>(),
		// Read again once the registries answer, so a label stored by name is turned into its id, and the last-seen
		// cache is sampled from the model this connection filled.
		() =>
		{
			services.GetRequiredService<HeatingDocumentHost>().Reload();

			if (services.GetRequiredService<TheHouseAsItIs>().Context is { } house)
			{
				services.GetRequiredService<LastSeenService>().CameUp(house);
				services.GetRequiredService<ChangesAtTheThermostats>().CameUp(house);
				services.GetRequiredService<AreaSnapshotCache>().CameUp(house);
			}
		},
		() =>
		{
			services.GetRequiredService<LastSeenService>().HasGone();
			services.GetRequiredService<ChangesAtTheThermostats>().HasGone();
			services.GetRequiredService<AreaSnapshotCache>().HasGone();
		}));

// Lighting's last-seen cache, in its last-seen folder beside the document: a sensor silent before a core restart
// stays silent after it.
builder.Services.AddEntityLastSeen();

// Who changed each thermostat, sorted as the change arrives; also the room event, which teaches it the add-on's own user.
builder.Services.AddSingleton(services =>
	new ChangesAtTheThermostats(
		new HaStatePublisher(
			() => services.GetRequiredService<TheHouseAsItIs>().Context,
			services.GetRequiredService<ILogger<HaStatePublisher>>()),
		services.GetRequiredService<ILogger<ChangesAtTheThermostats>>()));

// Each room's last change at its thermostat and who made it, in lighting's room-history note.
builder.Services.AddSingleton(services =>
	new RoomHistory(Placed(
		() => new RoomHistoryStore(services.GetRequiredService<StateStoreRegistry>(), services.GetRequiredService<ILoggerFactory>()),
		services,
		"Could not place the note recording each room's history, beside {Path}. Every room's history "
		+ "starts again as unknown after a restart.")));

// One instance for the life of the process, so an action reported as unknown is reported once across the
// whole add-on rather than once per caller.
builder.Services.AddSingleton(services =>
	new CoreApi(
		services.GetRequiredService<CoreConnection>(),
		services.GetRequiredService<ILogger<CoreApi>>()));

// Lighting's command log, written beside the document while the House tab has it switched on.
builder.Services.AddSingleton(services =>
{
	HeatingDocumentHost host = services.GetRequiredService<HeatingDocumentHost>();
	Lazy<CommandLog> log = new(() => new CommandLog(
		CommandLog.DirectoryBeside(host.Store.FilePath),
		services.GetRequiredService<ILogger<CommandLog>>()));

	return new Thermostats(
		services.GetRequiredService<CoreApi>(),
		() => host.Current.Global.CommandLog ? log.Value : null,
		() => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, household.Zone));
});

// Each room's own settings, sent against that room's own thermostat and only where they have moved.
builder.Services.AddSingleton<TellsEachRoomItsSettings>();

// Reading a calendar and adding an entry to one. Home Assistant offers nothing else for a calendar, which is
// what makes the record durable.
builder.Services.AddSingleton<Calendars>();

// Which integration keeps each calendar, read from the entity registry once per connection and only where the
// house reports a calendar at all.
builder.Services.AddSingleton(services =>
	new TheCalendarsOnOffer(services.GetRequiredService<TheHouseAsItIs>().About));

builder.Services.AddSingleton<TellsTheThermostats>(services =>
	new TellsTheThermostats(
		services.GetRequiredService<DurableSettingsStore>(),
		services.GetRequiredService<Thermostats>(),
		services.GetRequiredService<ILogger<TellsTheThermostats>>()));

builder.Services.AddSingleton<ISettingsStore>(services => services.GetRequiredService<TellsTheThermostats>());

// The activity record: bounded, and journalled into a file beside the settings so a restart does not lose what
// the add-on did. Declared in the registry the settings file already uses, so one flusher covers both.
builder.Services.AddSingleton(services =>
	new ActivityJournal(
		services.GetRequiredService<StateStoreRegistry>(),
		services.GetRequiredService<TimeProvider>(),
		services.GetRequiredService<ILogger<ActivityJournal>>()));

builder.Services.AddSingleton<IActivityJournal>(services => services.GetRequiredService<ActivityJournal>());

builder.Services.AddSingleton(services =>
	new ActivityRecord(services.GetRequiredService<IActivityJournal>()));

builder.Services.AddSingleton<ITellsAPerson>(services =>
	new TellsAPerson(
		services.GetRequiredService<CoreApi>(),
		services.GetRequiredService<ILogger<TellsAPerson>>()));

// Home Assistant's own record of what happened in the house, which every report is written to as well.
builder.Services.AddSingleton<ITellsTheLogbook>(services =>
	new TellsTheLogbook(
		services.GetRequiredService<CoreApi>(),
		services.GetRequiredService<ILogger<TellsTheLogbook>>()));

builder.Services.AddSingleton(services =>
	new ReportsWhatHappened(
		services.GetRequiredService<ActivityRecord>(),
		services.GetRequiredService<ITellsAPerson>(),
		services.GetRequiredService<ITellsTheLogbook>(),
		// The record holds the room's id; what a person reads is looked up when the words are built, so a
		// rename shows at once and never splits one room's history in two.
		roomId => services.GetRequiredService<ISettingsStore>().Read().Rooms
			.FirstOrDefault(room => string.Equals(room.Id, roomId, StringComparison.Ordinal))?.Name ?? roomId,
		// The room's own entity, so the Activity panel beside a room shows that room's lines alone.
		roomId => services.GetRequiredService<ISettingsStore>().Read().Rooms
			.FirstOrDefault(room => string.Equals(room.Id, roomId, StringComparison.Ordinal))?.Thermostat,
		roomId => services.GetRequiredService<ISettingsStore>().Read().Rooms
			.FirstOrDefault(room => string.Equals(room.Id, roomId, StringComparison.Ordinal))?.AreaId,
		services.GetRequiredService<TheHouseholdClock>(),
		userId => services.GetRequiredService<ChangesAtTheThermostats>().PersonWords(userId),
		automation => services.GetRequiredService<ChangesAtTheThermostats>().AutomationWords(automation),
		services.GetRequiredService<ILogger<ReportsWhatHappened>>()));

// The room page's log fold reads the same record the logbook is written from.
builder.Services.AddSingleton<IRoomActivity>(services => services.GetRequiredService<ReportsWhatHappened>());

// The activity page and the dashboard's summary read that record too.
builder.Services.AddSingleton<IActivityRecord>(services => services.GetRequiredService<ReportsWhatHappened>());

// Lighting's read of the room event back from Home Assistant, which the dashboard draws.
builder.Services.AddSingleton<AreaSnapshotCache>();

builder.Services.AddSingleton(services =>
	new TheArrivals(
		services.GetRequiredService<Calendars>(),
		services.GetRequiredService<ReportsWhatHappened>(),
		services.GetRequiredService<ILogger<TheArrivals>>()));

// The warm-ups asked for and not yet written down, in a file beside the settings: a warm-up cannot be seen
// finishing from outside, so what was asked for has to survive a restart or the entry is lost. Declared in the
// registry the settings file already uses, so one flusher and one start-up report cover all three.
builder.Services.AddSingleton(services =>
	new WarmUpNote(
		services.GetRequiredService<StateStoreRegistry>(),
		services.GetRequiredService<TimeProvider>(),
		services.GetRequiredService<ILogger<WarmUpNote>>()));

builder.Services.AddSingleton(services =>
	new RecordsTheWarmUps(
		services.GetRequiredService<WarmUpNote>(),
		services.GetRequiredService<Calendars>(),
		services.GetRequiredService<ReportsWhatHappened>(),
		// The row holds the room's id; what a person reads is looked up when the entry is written, so a rename
		// shows at once and never splits one room's record in two.
		roomId => services.GetRequiredService<ISettingsStore>().Read().Rooms
			.FirstOrDefault(room => string.Equals(room.Id, roomId, StringComparison.Ordinal))?.Name ?? roomId));

// The house mode every room runs, read by lighting's rules from the selector the document names.
builder.Services.AddSingleton(services =>
{
	HaCatalog sun = new(
		() => services.GetRequiredService<TheHouseAsItIs>().Context,
		() => services.GetRequiredService<TheHouseAsItIs>().Registry,
		services.GetRequiredService<ILoggerFactory>());

	return new TheHouseMode(
		() => services.GetRequiredService<TheHouseAsItIs>().Context,
		() => services.GetRequiredService<HeatingDocumentHost>().Current,
		services.GetRequiredService<IScheduler>(),
		AddOnSwitch.EntityId,
		services.GetRequiredService<ILoggerFactory>(),
		// The sun the page reads too, so the engine and the page agree on when a period begins.
		() =>
		{
			(TimeOnly? sunrise, TimeOnly? sunset) = sun.SunTimesToday(
				household.Zone,
				services.GetRequiredService<HeatingDocumentHost>().Current.Global.SunEntity);
			return new SunTimes(sunrise, sunset);
		},
		Placed(
			() => new LastPeriodStore(
				documentPath,
				services.GetRequiredService<ILogger<LastPeriodStore>>(),
				services.GetRequiredService<StateStoreRegistry>()),
			services,
			"Could not place the note recording which circadian period the engine is in, beside {Path}. A period's "
			+ "house mode will be applied only at a boundary the engine is running to see."),
		household.Zone,
		() => services.GetRequiredService<ConnectionToTheIntegration>().OnePassPlease());
});

builder.Services.AddSingleton(services =>
	new DrivesTheRooms(
		services.GetRequiredService<ISettingsStore>(),
		services.GetRequiredService<Thermostats>(),
		services.GetRequiredService<CoreApi>(),
		PlannerDefaults.Standard,
		household,
		() => services.GetRequiredService<HeatingDocumentHost>().Current,
		services.GetRequiredService<ChangesAtTheThermostats>(),
		services.GetRequiredService<ReportsWhatHappened>(),
		services.GetRequiredService<TheArrivals>(),
		services.GetRequiredService<RecordsTheWarmUps>(),
		services.GetRequiredService<TheHouseMode>(),
		services.GetRequiredService<ILogger<DrivesTheRooms>>(),
		services.GetRequiredService<RoomHistory>(),
		services.GetRequiredService<IEntityLastSeen>(),
		services.GetRequiredService<ChangesAtTheThermostats>()));

// A switched-on room that cannot be set up is told once, remembered across a restart in a note beside the settings.
builder.Services.AddSingleton(services =>
	new RoomsThatCannotBeSetUp(
		Placed(
			() => new AreaSetupMemoryStore(
				documentPath,
				services.GetRequiredService<ILogger<AreaSetupMemoryStore>>(),
				services.GetRequiredService<StateStoreRegistry>()),
			services,
			"Could not place the note recording which rooms have already been reported as impossible to set up, "
			+ "beside {Path}. Any such room will be reported again at every start."),
		services.GetRequiredService<ITellsAPerson>(),
		services.GetRequiredService<ILogger<RoomsThatCannotBeSetUp>>()));

// Lighting's recorder: the start and every save are rows of the record. No card: nothing here needs a person.
builder.Services.AddHostedService(services =>
	new EngineNoticeRecorder(
		services.GetRequiredService<HeatingDocumentHost>(),
		notice => _ = services.GetRequiredService<ReportsWhatHappened>().ReportAsync(
			notice.Kind is EngineNoticeKind.Started
				? HeatingReport.About(WhatHappened.Started, notice.At) with { Named = HalfVersions.AddOnVersion }
				: HeatingReport.About(WhatHappened.SettingsSaved, notice.At),
			CancellationToken.None),
		services.GetRequiredService<ILogger<EngineNoticeRecorder>>()));

builder.Services.AddSingleton<RoomsMovedInHomeAssistant>();
builder.Services.AddSingleton<ConnectionToTheIntegration>();
builder.Services.AddHostedService(services => services.GetRequiredService<ConnectionToTheIntegration>());

builder.Services.AddLocalization();

// Home Assistant never tells the page which language a person chose, so the page picks from what the browser
// asks for and falls back to English. A language is added by dropping a resource file in, which builds into a
// satellite assembly PageLanguages finds on disk.
IReadOnlyList<CultureInfo> languages = PageLanguages.Available();

builder.Services.Configure<RequestLocalizationOptions>(options =>
{
	options.DefaultRequestCulture = new RequestCulture(PageLanguages.Fallback);
	options.SupportedCultures = [.. languages];
	options.SupportedUICultures = [.. languages];
	options.ApplyCurrentCultureToResponseHeaders = true;
});

builder.Services.AddAuthentication(IngressIdentity.Scheme)
	.AddScheme<AuthenticationSchemeOptions, IngressAuthenticationHandler>(IngressIdentity.Scheme, configureOptions: null);

builder.Services.AddAuthorization();

// Named after the page rather than taking the framework's default: cookies are not scoped by port, so two
// applications on one host holding the default name overwrite each other's.
builder.Services.AddAntiforgery(options => options.Cookie.Name = "adaptive-heating.antiforgery");

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

WebApplication app = builder.Build();

// Reading the settings is what opens the file, so it happens before the report rather than on the first
// request. Without the report a person cannot tell a restored setting from a fresh one after a deploy.
_ = app.Services.GetRequiredService<ISettingsStore>();

// Reading the record is what opens the journal, and it happens before the report for the same reason.
_ = app.Services.GetRequiredService<ActivityRecord>();

// The warm-ups awaiting the record open their own file the same way, so the report names all three.
_ = app.Services.GetRequiredService<RecordsTheWarmUps>();

// The room-history note and the last-seen cache are declared in the same registry, so the report names them too.
_ = app.Services.GetRequiredService<RoomHistory>();
_ = app.Services.GetRequiredService<LastSeenStore>();

// Reading the document opens its file and seeds it on a first run, and the resolve logs where that file is, so
// both happen before the start-up report rather than on the first request.
app.Services.GetRequiredService<HeatingDocumentHost>().Reload();

app.Services.GetRequiredService<StateStoreRegistry>().ReportRestored();

app.UseRequestLocalization();

// No path-base middleware anywhere: Home Assistant strips its prefix before the request arrives, so every
// route matches as though the page sat at the root of its own address. Stripping it again would strip a
// prefix that has already gone.
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// Lighting's frame rule: the document's embed list may show the page inside a dashboard card.
app.UseLightingFrameEmbedding();

app.MapStaticAssets();
app.MapRazorComponents<HeatingApp>().AddInteractiveServerRenderMode();

app.Run();

// Lighting's rule for the notes beside the document: a path one cannot be written beside costs that note, never the add-on.
T? Placed<T>(Func<T> open, IServiceProvider services, string warning) where T : class
{
	try
	{
		return open();
	}
	catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
	{
		services.GetRequiredService<ILoggerFactory>().CreateLogger("AdaptiveHeating.AddOn").LogWarning(exception, warning, documentPath);

		return null;
	}
}
