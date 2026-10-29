using AdaptiveHeating.Page.Settings;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;

using NetDaemon.HassModel.Entities;

using HeatingMode = AdaptiveHeating.Planner.HeatingMode;
using HeatingModes = AdaptiveHeating.Planner.HeatingModes;

namespace AdaptiveLighting.Web.Presentation;

/// <summary>What the save line is saying, as a meaning rather than a class name.</summary>
public enum RoomSaveState
{
	/// <summary>Nothing to say.</summary>
	Settled,

	/// <summary>An edit is waiting for the quiet window to run out.</summary>
	Waiting,

	/// <summary>The last write landed, and the confirmation is still up.</summary>
	Saved,

	/// <summary>The last write was refused and the file is not what is on screen.</summary>
	Refused
}

/// <summary>Everything one room page holds, decides and writes; a plain class, so a design puts its own markup over it.</summary>
public sealed class RoomPageModel : IPageClock, IDisposable
{
	/// <summary>How long the page waits for the hand to settle before writing.</summary>
	private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(1200);

	/// <summary>How long the write confirmation stays on the save line.</summary>
	private static readonly TimeSpan ConfirmationLingers = TimeSpan.FromSeconds(3.5);

	/// <summary>How many of this room's log entries the card holds.</summary>
	private const int LogRows = 6;

	/// <summary>What the pickers call the second block of each list.</summary>
	public const string OthersLabel = "Everything else in this room";

	/// <summary>What the interface asks before leaving with an edit that could not be written.</summary>
	public const string LeaveQuestion = "Leave this room? The change that could not be saved will be lost.";

	private readonly HeatingDocumentHost _engine;
	private readonly HaCatalog _catalog;
	private readonly IAreaRegistry _areas;
	private readonly ISettingsStore _stamps;
	private readonly IStampSource _clock;
	private readonly IRoomActivity _log;
	private readonly TimeZoneInfo _zone;
	private readonly ILogger _logger;
	private readonly Func<Action, Task> _dispatch;

	private string _areaId = "";
	private string? _loadedFor;

	// A field, not a getter: it parses the schedule or reads Home Assistant, and the page renders once a second.
	private TimePeriodConfig? _activePeriod;

	// The one slot of the document this page may write, and that slot as it stood on disk when it was read.
	private RoomWriteToken _write = new(null, "");

	// The values each mode reads, as last written, so a save stamps only the modes whose value moved.
	private string[] _written = [];

	private TransientMessage _saved = TransientMessage.None;
	private CancellationTokenSource? _saveCts;

	// Per-visit, never persisted: the note is advice about this house right now.
	private bool _switchOnDismissed;

	// Everything Home Assistant has, read once per document load. The pickers' fallback, not what they show.
	private IReadOnlyList<EntityOption> _heaters = [];
	private IReadOnlyList<EntityOption> _sensors = [];

	private string? _reasonSensor;
	private string _still = "";

	private EntityLookup? _lookup;
	private Func<string, string>? _nameOf;
	private Func<string, string>? _areaNameOf;

	// Set while the removal is being written, so the write takes the room's slot out instead of putting it back.
	private bool _removed;

	private IDisposable? _ticker;

	/// <summary>Builds the model over the services one room page needs.</summary>
	public RoomPageModel(
		HeatingDocumentHost engine,
		HaCatalog catalog,
		IAreaRegistry areas,
		ISettingsStore stamps,
		IStampSource clock,
		IRoomActivity log,
		TimeZoneInfo zone,
		ILogger logger,
		Func<Action, Task> dispatch)
	{
		_log = log ?? throw new ArgumentNullException(nameof(log));
		_engine = engine ?? throw new ArgumentNullException(nameof(engine));
		_catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
		_areas = areas ?? throw new ArgumentNullException(nameof(areas));
		_stamps = stamps ?? throw new ArgumentNullException(nameof(stamps));
		_clock = clock ?? throw new ArgumentNullException(nameof(clock));
		_zone = zone ?? throw new ArgumentNullException(nameof(zone));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		_dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
	}

	/// <summary>Raised when a value the still parts of the page show has moved: a delayed write, the period in force.</summary>
	public event Action? Changed;

	/// <inheritdoc/>
	public event Action? Tick;

	/// <summary>Starts the one-second clock.</summary>
	public void Start()
	{
		// Once a second, because the relative times are only honest while they move. The beat alone redraws only
		// what listens to it; the rest of the page waits for a value to actually move.
		_ticker = Observable.Interval(TimeSpan.FromSeconds(1))
			.SubscribeSafe(beat => Beat(), _logger);
	}

	private void Beat() => _ = _dispatch(() =>
	{
		Now = DateTimeOffset.Now;

		if (ReadLiveState())
			Changed?.Invoke();

		Tick?.Invoke();
	});

	/// <summary>Points the model at a room, reloading only when the route names a different one.</summary>
	public void Show(string areaId)
	{
		_areaId = areaId ?? "";

		if (string.Equals(_loadedFor, _areaId, StringComparison.Ordinal))
			return;

		Load();
	}

	// ---- what the page is looking at -------------------------------------------------------------------

	public HeatingConfig Document { get; private set; } = new();

	public AreaConfig? Area { get; private set; }

	public string? LoadError { get; private set; }

	/// <summary>The schedule, which the levels table draws.</summary>
	public List<TimePeriodConfig> Periods => Document.Periods;

	/// <summary>Whether the temperature card has anything to show: a room with no reading takes on or off instead.</summary>
	public bool ShowLevelsCard => Slice?.NoReadingAtAll is not true;

	public DateTimeOffset Now { get; private set; } = DateTimeOffset.Now;

	public ValidationResult? Validation { get; private set; }

	/// <summary>The last refused write, which stands until it is resolved.</summary>
	public SaveResult? Failure { get; private set; }

	/// <summary>What this room is called, everywhere on this page.</summary>
	public string RoomName => Area is { } room ? AreaNaming.DisplayName(room, _areas) : _areaId;

	public string PageName => Area is null ? _areaId : RoomName;

	public string RequestedAreaId => _areaId;

	/// <summary>The house default settings this room inherits from.</summary>
	public AreaSettings Defaults => Document.Defaults;

	/// <summary>The room's settings with its inheritance resolved.</summary>
	public AreaSettings Effective => Area is { } room ? room.Effective(Defaults) : Defaults;

	/// <summary>Whether the add-on commands this room at all.</summary>
	public bool IsEnabled => Area is { } room && room.Effective(Defaults).Enabled;

	/// <summary>What discovery resolves in this room right now.</summary>
	public AreaPreview Preview { get; private set; } = new(null, null);

	/// <summary>The heaters and sensors this room resolves to, or <c>null</c> when discovery could not answer.</summary>
	public Adaptive.Engine.ResolvedRoom? Resolved => Preview.Resolved;

	/// <summary>Why discovery could not answer.</summary>
	public string? PreviewError => Preview.Error;

	/// <summary>Whether Home Assistant came back with anything.</summary>
	public bool HomeAssistantIsAnswering { get; private set; }

	/// <summary>This room's slice of the settings store: its sensor values, its on-or-off cells, its thermostat.</summary>
	public RoomSlice? Slice => _stamps.Read().RoomIn(_areaId);

	// What everything kept per room is keyed by. Empty until the integration names a room in this area.
	private string RoomId => Slice?.Id ?? "";

	/// <summary>A friendly name for an entity id, with the id as the fallback.</summary>
	// Built once: a fresh delegate each render would hand every control a changed parameter on every tick.
	public Func<string, string> NameOf => _nameOf ??= entityId => _catalog.FriendlyNameOrId(entityId);

	/// <summary>What every picker is told about an id it did not offer.</summary>
	public EntityLookup Lookup => _lookup ??= new EntityLookup(NameOf, _catalog.Knows);

	// ---- the header ------------------------------------------------------------------------------------

	/// <summary>What the room's thermostat and reason sensor publish now, or <c>null</c> before either reports.</summary>
	public RoomCard? Card { get; private set; }

	/// <summary>What the add-on's last pass decided about this room's own gates.</summary>
	public RoomVerdict? Verdict => _stamps.Read().VerdictFor(RoomId);

	/// <summary>Whether the thermostat has reported at all.</summary>
	public bool HasReport => Card is not null;

	public string Since => Card is { } card ? RoomFacts.Since(card, Now) : string.Empty;

	public string Headline => Card is { } card ? RoomFacts.Headline(card, Verdict) : string.Empty;

	public string? NextLine => Card is { } card ? RoomFacts.NextLine(card, Verdict, Now, NameOf) : null;

	public string? NotResponding => Card is { } card ? RoomFacts.NotResponding(card) : null;

	public string? Fault => Card is { } card ? RoomFacts.Fault(card) : null;

	public IReadOnlyList<SensorWarning> SensorWarnings => Card is { } card ? RoomFacts.SensorWarnings(card, NameOf) : [];

	/// <summary>The temperature sensors with a low battery, read live off each sensor's own device.</summary>
	public IReadOnlyList<SensorBattery> LowBatteryReadings { get; private set; } = [];

	/// <summary>One sentence per temperature sensor with a low battery.</summary>
	public IReadOnlyList<string> LowBatteries => RoomFacts.LowBatteries(LowBatteryReadings, NameOf);

	/// <summary>The facts table beside the header.</summary>
	public IReadOnlyList<RoomFact> Facts =>
		Card is { } card ? [.. RoomFacts.For(card, Verdict, _activePeriod?.Name, Now, NameOf), Told] : [];

	// What the add-on is telling the room, which is not always what it holds: a warm-up or a hold in the room wins.
	private RoomFact Told => _stamps.Read().ToldFor(RoomId) is { } told
		? new RoomFact("Told", $"{told.Value.ToString("0.#", CultureInfo.CurrentCulture)} °C", "What this half is telling the room. A warm-up or a hold running in the room wins over it.")
		: new RoomFact("Told", "nothing yet", "What this half is telling the room. A warm-up or a hold running in the room wins over it.");

	/// <summary>What the adaptive-heating switch promises.</summary>
	public string SwitchTitle => IsEnabled
		? "On — the add-on heats this room by its modes and schedule. Switching it off leaves the thermostat where it is."
		: "Off — this room is watched, but its thermostat is never changed. Its settings are kept and take effect the moment it is enabled again.";

	/// <summary>The advice raised by switching the room on, until it is put away.</summary>
	public SwitchOnNote? SwitchOnNote { get; private set; }

	/// <summary>Turns adaptive heating on or off for this room.</summary>
	public async Task ToggleEnabled()
	{
		if (Area is not { } room)
			return;

		bool switchingOn = !IsEnabled;
		room.Enabled = switchingOn;

		await MarkDirty().ConfigureAwait(false);

		// A switch is one press, and waiting out the quiet window leaves the room not running for a second after it reads on.
		Commit();

		SwitchOnNote = switchingOn && !_switchOnDismissed
			? SwitchOnWarning.For(RoomName, HeatersUnderReview(), Slice?.NoReadingAtAll is not true)
			: null;
	}

	/// <summary>Puts the switch-on note away for the rest of this visit.</summary>
	public void DismissSwitchOnNote()
	{
		SwitchOnNote = null;
		_switchOnDismissed = true;
	}

	private List<HeaterUnderReview> HeatersUnderReview() =>
		[.. (Resolved?.Heaters ?? []).Select(heater => new HeaterUnderReview(
			heater,
			NameOf(heater),
			_catalog.CurrentStateOf(heater) is not null))];

	// ---- saving ----------------------------------------------------------------------------------------

	public bool Dirty { get; private set; }

	/// <summary>Everything this page has to say about saving, in one line.</summary>
	public string SaveLine => SaveState switch
	{
		RoomSaveState.Refused => "not saved",
		RoomSaveState.Waiting => "saving in a moment…",
		RoomSaveState.Saved => _saved.Text,
		_ => string.Empty
	};

	public TransientMessage SaveConfirmation => _saved;

	/// <summary>The same answer as a meaning, for a design to paint how it likes.</summary>
	// Order is the ranking: a refusal stands until it is resolved, and a fresh edit outranks the confirmation of
	// the one before it.
	public RoomSaveState SaveState =>
		Failure is not null ? RoomSaveState.Refused
		: Dirty ? RoomSaveState.Waiting
		: _saved.IsShownAt(Now) ? RoomSaveState.Saved
		: RoomSaveState.Settled;

	public bool HasRefusedEdit => Dirty && Failure is not null;

	/// <summary>The validator's complaint about this room, when it has one.</summary>
	public AreaError? AreaError =>
		Validation is { } validation && Area is { } room
			? validation.AreaErrors.FirstOrDefault(error =>
				string.Equals(error.AreaName, room.DisplayName, StringComparison.OrdinalIgnoreCase))
			: null;

	/// <summary>Records that the document has moved ahead of the file, and starts the timer that catches it up.</summary>
	public Task MarkDirty()
	{
		Dirty = true;
		Refresh();

		_saveCts?.Cancel();
		_saveCts?.Dispose();
		_saveCts = new CancellationTokenSource();

		// Fire and forget. SaveAfterQuiet swallows its own cancellation and logs the rest, so nothing here can
		// surface as an unobserved exception on a thread-pool thread.
		_ = SaveAfterQuiet(_saveCts.Token);

		return Task.CompletedTask;
	}

	private async Task SaveAfterQuiet(CancellationToken token)
	{
		try
		{
			await Task.Delay(SaveDelay, token).ConfigureAwait(false);

			await _dispatch(() =>
			{
				Commit();
				Changed?.Invoke();
			}).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			// A newer edit superseded this timer, or the page went away. Either way there is nothing to write here.
		}
		catch (ObjectDisposedException)
		{
			// The circuit closed while the timer was running; Dispose has already written whatever was pending.
		}
		catch (Exception exception)
		{
			_logger.LogError(exception, "The room page could not save after an edit.");
		}
	}

	/// <summary>Writes this room into the document as it is on disk now, through the one save pipeline.</summary>
	public void Commit()
	{
		if (!Dirty || LoadError is { Length: > 0 })
			return;

		_saveCts?.Cancel();

		try
		{
			RoomWriteResult write = RoomWrite.Save(_engine, _write, _removed ? null : Area);
			SaveResult result = write.Result;
			_write = write.Token;

			// A conflict carries no verdict on this document, so it must not replace the one on screen.
			if (result.Status is not SaveStatus.Conflicted)
				Validation = result.Validation;

			if (result.Written)
			{
				DateTimeOffset saved = DateTimeOffset.Now;

				Dirty = false;
				_saved = TransientMessage.For(
					$"Saved {saved.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture)}",
					saved,
					ConfirmationLingers);
				Failure = null;

				StampWhatMoved();
			}
			else
			{
				// Drops any lingering confirmation, so a refusal never stands beside a stale "Saved".
				_saved = TransientMessage.None;
				Failure = result;
			}
		}
		catch (HeatingConfigException exception)
		{
			LoadError = exception.Message;
		}
	}

	public void Discard()
	{
		_loadedFor = null;
		Show(_areaId);
	}

	// ---- temperatures ----------------------------------------------------------------------------------

	public int OwnPeriodCount => Area is { } room ? RoomLevels.OwnCount(Periods, room) : 0;

	/// <summary>The line under the card's head: how much of the schedule this room has taken over, in words.</summary>
	public string LevelsLede
	{
		get
		{
			if (Area is not { } room)
				return string.Empty;

			int periods = Periods.Count;
			int chosen = RoomLevels.OwnCount(Periods, room);
			int orphaned = RoomLevels.Orphans(Periods, room).Count;

			// Orphans are counted apart, never folded in: they are not among the periods on screen.
			string kept = orphaned switch
			{
				0 => string.Empty,
				1 => " 1 kept from a period that is gone.",
				_ => $" {orphaned} kept from periods that are gone."
			};

			if (periods == 0)
				return "The schedule has no periods." + kept;

			if (chosen == 0)
				return "All follow the schedule." + kept;

			return chosen == periods
				? $"All {periods} set here." + kept
				: $"{chosen} of {periods} set here." + kept;
		}
	}

	/// <summary>The period in force right now by id, which is what the levels table matches its rows on.</summary>
	public string? ActivePeriodId => _activePeriod?.Key;

	public double? AwayTemperature => Area?.AwayTemperature;

	public double? BoostTemperature => Area?.BoostTemperature;

	public Task SetAwayTemperature(double? value) => Set(() => Area!.AwayTemperature = value);

	public Task SetBoostTemperature(double? value) => Set(() => Area!.BoostTemperature = value);

	// ---- on or off per mode, for a room with no reading -------------------------------------------------

	/// <summary>Whether a room with no reading has its heaters on in <paramref name="mode"/>.</summary>
	public bool SwitchedOnIn(HeatingMode mode) =>
		Slice?.ModeSwitches.TryGetValue(mode, out AdaptiveHeating.Planner.Stamped<bool> held) is true && held.Value;

	/// <summary>The modes in the order the cells are drawn.</summary>
	public static IReadOnlyList<HeatingMode> Modes => HeatingModes.All;

	/// <summary>Why the last on-or-off press was refused, or <c>null</c>.</summary>
	public string? SwitchRefused { get; private set; }

	/// <summary>Sets one mode's cell, stamped now, through the settings store.</summary>
	public void SetSwitchedOn(HeatingMode mode, bool on)
	{
		SaveOutcome outcome = _stamps.SaveModeSwitch(RoomId, mode, on, _clock.Take());
		SwitchRefused = outcome.Written ? null : outcome.Message;
		Changed?.Invoke();
	}

	// ---- the room's own settings -----------------------------------------------------------------------

	/// <summary>How long a change at the thermostat holds the room, in minutes.</summary>
	public int HoldMinutes => Effective.OverrideDurationMinutes;

	public bool HoldIsOwn => Area?.OverrideDurationMinutes is not null;

	public string HoldHouseText => $"{Defaults.OverrideDurationMinutes} min";

	/// <summary>Sets the room's own hold length, or clears it with <c>null</c>.</summary>
	public Task SetHoldMinutes(int? minutes) => Set(() => Area!.OverrideDurationMinutes = minutes);

	/// <summary>What pauses this room.</summary>
	public IReadOnlyList<string> PauseWhenOn => Area?.PauseWhenOn ?? [];

	/// <summary>Whether that list holds while its entities are off instead.</summary>
	public bool PauseWhenOnInverted => Area?.PauseWhenOnInverted is true;

	/// <summary>What holds this room at its temperature.</summary>
	public IReadOnlyList<string> HoldWhenOn => Area?.HoldWhenOn ?? [];

	/// <summary>Whether that list holds while its entities are off instead.</summary>
	public bool HoldWhenOnInverted => Area?.HoldWhenOnInverted is true;

	/// <summary>Everything that could pause or hold a room.</summary>
	public IReadOnlyList<EntityOption> BlockerOptions { get; private set; } = [];

	public Task SetPauseWhenOn(IReadOnlyList<string> value) => SetList(value, list => Area!.PauseWhenOn = list);

	public Task SetPauseWhenOnInverted(bool inverted) => SetInvert(inverted, value => Area!.PauseWhenOnInverted = value);

	public Task SetHoldWhenOn(IReadOnlyList<string> value) => SetList(value, list => Area!.HoldWhenOn = list);

	public Task SetHoldWhenOnInverted(bool inverted) => SetInvert(inverted, value => Area!.HoldWhenOnInverted = value);

	/// <summary>The room's own answer on automations, or the house's while it states none.</summary>
	public bool AutomationsCountAsManual => Area?.TreatAutomationsAsManual ?? Document.Global.TreatAutomationsAsManual;

	/// <summary>Whether that answer is this room's own.</summary>
	public bool AutomationsAreOwn => Area?.TreatAutomationsAsManual is not null;

	/// <summary>What the house says, for the road back.</summary>
	public string AutomationsHouseText => Document.Global.TreatAutomationsAsManual ? "yes" : "no";

	/// <summary>Sets the room's answer on automations, or clears it with <c>null</c>.</summary>
	public Task SetAutomationsAsManual(bool? value) => Set(() => Area!.TreatAutomationsAsManual = value);

	// ---- night setback ---------------------------------------------------------------------------------

	/// <summary>Offered only to a room with a temperature sensor and a power meter of its own.</summary>
	public bool OffersNightSetback => Resolved is { TemperatureSensors.Count: > 0, PowerSensor.Length: > 0 };

	public bool NightSetbackOn => Area?.NightSetback is true;

	public Task SetNightSetback(bool on) => Set(() => Area!.NightSetback = on ? true : null);

	public double LowestNightTemperature => Effective.LowestNightTemperature;

	public bool LowestNightIsOwn => Area?.LowestNightTemperature is not null;

	public string LowestNightHouseText => Degrees(Defaults.LowestNightTemperature);

	public Task SetLowestNightTemperature(double? value) => Set(() => Area!.LowestNightTemperature = value);

	public double FrostTemperature => Effective.FrostTemperature;

	public bool FrostProtection => Effective.FrostProtection;

	public bool FrostIsOwn => Area is { FrostTemperature: not null } or { FrostProtection: not null };

	public string FrostText => FrostWords(Effective);

	public string FrostHouseText => FrostWords(Defaults);

	public Task SetFrostTemperature(double value) => Set(() => Area!.FrostTemperature = value);

	public Task SetFrostProtection(bool on) => Set(() => Area!.FrostProtection = on);

	/// <summary>Back to the house's frost temperature and its limit.</summary>
	public Task RevertFrost() => Set(() =>
	{
		Area!.FrostTemperature = null;
		Area.FrostProtection = null;
	});

	private static string FrostWords(AreaSettings settings) =>
		settings.FrostProtection ? Degrees(settings.FrostTemperature) : "No limit";

	private static string Degrees(double value) => $"{value.ToString("0.#", CultureInfo.CurrentCulture)} °C";

	/// <summary>Why the last pass gave this room no setback, or what it gave, in a few words.</summary>
	public string? NightSetbackNow => Verdict?.NightSetback;

	/// <summary>The thermostat's own minimum where tonight's setback stops at it, or <c>null</c>.</summary>
	public double? NightSetbackThermostatFloor => NightSetbackOn ? Verdict?.SetbackThermostatFloor : null;

	/// <summary>What the room's sensors are held to, from the settings store.</summary>
	public RoomSensorSettings? SensorSettings => Slice?.Sensors;

	/// <summary>Why the last sensor setting was refused, or <c>null</c>.</summary>
	public string? SensorRefused { get; private set; }

	/// <summary>Writes the room's sensor values through the settings store, which carries them to the room.</summary>
	public void SetSensorSettings(Func<RoomSensorSettings, RoomSensorSettings> change)
	{
		ArgumentNullException.ThrowIfNull(change);

		if (SensorSettings is not { } held)
			return;

		SaveOutcome outcome = _stamps.SaveSensorSettings(RoomId, change(held));
		SensorRefused = outcome.Written ? null : outcome.Message;
		Changed?.Invoke();
	}

	// ---- room identity ---------------------------------------------------------------------------------

	/// <summary>The name this room states for itself, empty while it follows the area's.</summary>
	public string? OwnName => Area?.Name;

	/// <summary>The Home Assistant area this room is.</summary>
	public string? AreaId => Area?.AreaId;

	/// <summary>Renames the room, or clears the name so it follows the area's again.</summary>
	public Task SetOwnName(string? typed)
	{
		if (Area is not { } room)
			return Task.CompletedTask;

		room.Name = string.IsNullOrWhiteSpace(typed) ? null : typed.Trim();

		return MarkDirty();
	}

	// ---- what is in the room ---------------------------------------------------------------------------

	/// <summary>What has been left out of this room by hand.</summary>
	public IReadOnlyList<string> Excluded => Area?.ExcludeEntities ?? [];

	/// <summary>Drops one discovered entity from this room without switching the room to a hand-picked list.</summary>
	public Task Exclude(string entityId)
	{
		if (Area is not { } room)
			return Task.CompletedTask;

		List<string> excluded = room.ExcludeEntities is { } existing ? [.. existing] : [];

		if (!excluded.Contains(entityId, StringComparer.Ordinal))
			excluded.Add(entityId);

		room.ExcludeEntities = excluded;

		return MarkDirty();
	}

	/// <summary>The way back from <see cref="Exclude"/>.</summary>
	public Task Include(string entityId)
	{
		if (Area is not { } room || room.ExcludeEntities is not { } excluded)
			return Task.CompletedTask;

		List<string> kept = [.. excluded.Where(existing => !string.Equals(existing, entityId, StringComparison.Ordinal))];
		room.ExcludeEntities = kept.Count == 0 ? null : kept;

		return MarkDirty();
	}

	/// <summary>Whether the pickers are filtered to this room's own entities.</summary>
	public bool ScopeToArea => Area?.AreaId is { Length: > 0 };

	/// <summary>The line under the pickers: what the lists are showing right now.</summary>
	public string ScopeNote => ScopeToArea ? "This room's entities" : "Every entity in the house";

	/// <summary>Why a picker has nothing to offer; an empty scoped list is ordinary.</summary>
	public string EmptyNote(string what) =>
		!HomeAssistantIsAnswering ? "Waiting for Home Assistant — type an id"
		: ScopeToArea ? $"No {what} in this room — type an id"
		: $"No {what} in Home Assistant — type an id";

	public IReadOnlyList<string> Heaters => Area?.Heaters ?? [];

	public IReadOnlyList<string> TemperatureSensors => Area?.TemperatureSensors ?? [];

	public string? WindowSensor => Area?.WindowSensor;

	public string? PowerSensor => Area?.PowerSensor;

	public Task SetHeaters(IReadOnlyList<string> value) => SetList(value, list => Area!.Heaters = list);

	public Task SetTemperatureSensors(IReadOnlyList<string> value) => SetList(value, list => Area!.TemperatureSensors = list);

	public Task SetWindowSensor(string? value) => Set(() => Area!.WindowSensor = string.IsNullOrWhiteSpace(value) ? null : value);

	public Task SetPowerSensor(string? value) => Set(() => Area!.PowerSensor = string.IsNullOrWhiteSpace(value) ? null : value);

	/// <summary>What the heaters picker offers first.</summary>
	public IReadOnlyList<EntityOption> HeaterChoices { get; private set; } = [];

	/// <summary>What the temperature sensors picker offers first.</summary>
	public IReadOnlyList<EntityOption> SensorChoices { get; private set; } = [];

	/// <summary>Everything else in the room, for the heaters picker.</summary>
	public IReadOnlyList<EntityOption> HeaterOthers { get; private set; } = [];

	/// <summary>Everything else in the room, for the temperature sensors picker.</summary>
	public IReadOnlyList<EntityOption> SensorOthers { get; private set; } = [];

	/// <summary>Every door, window and opening sensor in the house.</summary>
	public IReadOnlyList<EntityOption> WindowChoices { get; private set; } = [];

	/// <summary>Every power sensor in the house.</summary>
	public IReadOnlyList<EntityOption> PowerChoices { get; private set; } = [];

	// ---- the neighbour's reading -----------------------------------------------------------------------

	/// <summary>The room this one heats on the reading of, by area id, or <c>null</c>.</summary>
	public string? Neighbour => Area?.Neighbour;

	/// <summary>The rooms a reading can be borrowed from: every other room that does not borrow its own.</summary>
	public IReadOnlyList<(string AreaId, string Name)> NeighbourChoices =>
	[
		.. Document.Areas
			.Where(other => other.AreaId is { Length: > 0 } id
				&& !string.Equals(id, _areaId, StringComparison.Ordinal)
				&& other.Neighbour is not { Length: > 0 })
			.Select(other => (other.AreaId!, AreaNaming.DisplayName(other, _areas)))
			.OrderBy(choice => choice.Item2, StringComparer.CurrentCulture)
	];

	/// <summary>The neighbour's name as a person calls it, or <c>null</c> where none is named.</summary>
	public string? NeighbourName =>
		Neighbour is { Length: > 0 } id
			? Document.Areas.FirstOrDefault(other => string.Equals(other.AreaId, id, StringComparison.Ordinal)) is { } lender
				? AreaNaming.DisplayName(lender, _areas)
				: id
			: null;

	public Task SetNeighbour(string? areaId) => Set(() => Area!.Neighbour = string.IsNullOrWhiteSpace(areaId) ? null : areaId);

	// ---- the area, setting up again and removing -------------------------------------------------------

	/// <summary>The areas this room can move to: its own and every area no other room has claimed.</summary>
	public IReadOnlyList<AreaOption> Areas { get; private set; } = [];

	/// <summary>Raised with the page to open once this room has moved or gone.</summary>
	public event Action<string>? Navigated;

	/// <summary>Resolves an area id to the name Home Assistant shows, for the set-up-again panel.</summary>
	public Func<string, string> AreaNameOf => _areaNameOf ??= areaId =>
		Areas.FirstOrDefault(area => string.Equals(area.Id, areaId, StringComparison.Ordinal))?.Name ?? areaId;

	/// <summary>Moves the room to another Home Assistant area, with its thermostat and what it has learnt.</summary>
	/// <remarks>The new area's name is not copied into the document: names resolve from the registry at the point
	/// of use. A room always has an area, so an empty choice changes nothing.</remarks>
	public async Task ChangeArea(string? areaId)
	{
		if (Area is not { } room || areaId is not { Length: > 0 } || string.Equals(areaId, room.AreaId, StringComparison.Ordinal))
			return;

		// The thermostat goes first. A refusal leaves the room in its old area.
		string? from = room.AreaId;

		if (from is { Length: > 0 } && await _stamps.CarryRoomAsync(from, areaId) is { Written: false } refused)
		{
			Failure = new SaveResult(SaveStatus.Rejected, Validation ?? new ValidationResult(), refused.Message);

			return;
		}

		// One step behind the document's gate, so a pass over the rooms sees the link and the entry move together.
		_engine.Exclusively(() => MoveTheEntry(room, from, areaId));

		if (Failure is not null || LoadError is { Length: > 0 })
		{
			Refresh();

			return;
		}

		Navigated?.Invoke(HouseView.RoomHref(areaId)!);
	}

	private void MoveTheEntry(AreaConfig room, string? from, string to)
	{
		HeatingConfig onDisk = _engine.Current;
		bool linked = from is { Length: > 0 };

		// The add-on follows a thermostat to its new area on its own, and may have moved the entry once the integration answered.
		if (linked && !onDisk.Areas.Any(area => area.AreaId == from) && onDisk.Areas.Any(area => area.AreaId == to))
			_write = RoomWrite.Open(onDisk, to);

		if (linked)
			_stamps.LinkRoom(from!, to);

		room.AreaId = to;
		Dirty = true;
		Commit();

		// A refused save leaves the entry where it was, and the link goes back with it.
		if (linked && (Failure is not null || LoadError is { Length: > 0 }))
			_stamps.LinkRoom(to, from!);
	}

	/// <summary>Whether the set-up-again panel is open.</summary>
	public bool SetupOpen { get; private set; }

	/// <summary>Whether the removal question is on screen.</summary>
	public bool Removing { get; private set; }

	/// <summary>The scope the set-up-again panel starts with: this room alone.</summary>
	public IReadOnlyCollection<string> SetupScope => Area?.AreaId is { Length: > 0 } areaId ? [areaId] : [];

	public void OpenSetup()
	{
		Removing = false;
		SetupOpen = true;
	}

	public void CancelSetup() => SetupOpen = false;

	public void AskRemove() => Removing = true;

	public void KeepRoom() => Removing = false;

	/// <summary>The plan for setting this room up again, carrying nothing but this room.</summary>
	/// <remarks><see cref="AreaSetupService.Plan"/> proposes every unconfigured area it finds, whatever the scope.
	/// The extras are dropped here and not at apply time, so the panel's warning stays true to what confirming
	/// does.</remarks>
	public SetupPlan PlanSetup(IReadOnlyCollection<string> scope) =>
		_catalog.PlanSetup(Document, scope) with { NewAreas = [], NotQualifying = [] };

	/// <summary>Carries out a confirmed rebuild, saved at once instead of after the usual pause.</summary>
	/// <remarks><see cref="AreaSetupService.Apply"/> replaces the room object, so the model has to find it again.</remarks>
	public void ApplySetup(SetupPlan plan)
	{
		AreaSetupService.Apply(Document, plan);
		SetupOpen = false;

		Area = Document.Areas.FirstOrDefault(area => string.Equals(area.AreaId, _areaId, StringComparison.Ordinal));

		Dirty = true;
		Commit();
		Refresh();
	}

	/// <summary>Removes the room and leaves for the rooms list; this page's subject no longer exists.</summary>
	public void Remove()
	{
		if (Area is not { } room)
			return;

		Document.Areas.Remove(room);
		_removed = true;
		Dirty = true;
		Commit();

		if (Failure is null)
			Navigated?.Invoke("settings?section=house");
		else
		{
			_removed = false;
			Removing = false;
		}
	}

	// ---- what it has learnt ----------------------------------------------------------------------------

	/// <summary>The three numbers the add-on works out for this room, or <c>null</c> before the room is known.</summary>
	public RoomLearntNumbers? Learnt => Slice?.Learnt;

	/// <summary>Why the last change to a learnt number was refused, or <c>null</c>.</summary>
	public string? LearntRefused { get; private set; }

	public void ResetLearnt(LearntKind kind) => Learned(_stamps.ResetLearnt(RoomId, kind));

	/// <summary>A set value locks the number; <c>null</c> releases the lock.</summary>
	public void SetLearntByHand(LearntKind kind, double? value) => Learned(_stamps.SetLearntByHand(RoomId, kind, value));

	private void Learned(SaveOutcome outcome)
	{
		LearntRefused = outcome.Written ? null : outcome.Message;
		Changed?.Invoke();
	}

	// ---- what happened here ----------------------------------------------------------------------------

	private IReadOnlyList<RoomLogRow> RoomReports => Area is null ? [] : _log.For(RoomId);

	/// <summary>The rows the log card draws.</summary>
	public IReadOnlyList<RoomLogRow> RoomLog => [.. RoomReports.Take(LogRows)];

	/// <summary>What the log card is holding back.</summary>
	public string LogFoot
	{
		get
		{
			int total = RoomReports.Count;
			int shown = RoomLog.Count;

			return shown >= LogRows && total > shown
				? $"newest {shown} of {total} · rest on Activity"
				: "all since start";
		}
	}

	// ---- loading and deriving --------------------------------------------------------------------------

	private void Load()
	{
		_loadedFor = _areaId;
		Failure = null;
		Dirty = false;
		_saved = TransientMessage.None;
		SwitchOnNote = null;
		_switchOnDismissed = false;

		try
		{
			Document = _engine.Store.Load();
			LoadError = null;
		}
		catch (HeatingConfigException exception)
		{
			LoadError = exception.Message;
			Area = null;

			return;
		}

		Area = Document.Areas.FirstOrDefault(area => string.Equals(area.AreaId, _areaId, StringComparison.Ordinal));
		_write = RoomWrite.Open(Document, _areaId);
		_written = WhatEachModeReads(Area);

		LoadCatalog();
		Refresh();
	}

	/// <summary>Re-reads the registry once per document load, never once per render.</summary>
	private void LoadCatalog()
	{
		BlockerOptions = _catalog.EntitiesInDomains("binary_sensor", "input_boolean", "switch", "media_player");
		WindowChoices = _catalog.EntitiesWithDeviceClass("binary_sensor", ["door", "window", "opening", "garage_door"]);
		PowerChoices = _catalog.EntitiesWithDeviceClass("sensor", ["power"]);
		_heaters = _catalog.Heaters();
		_sensors = _catalog.TemperatureSensors();
		HomeAssistantIsAnswering = _catalog.IsHomeAssistantResponding(Document.Global);
		_reasonSensor = _catalog.ReasonSensorIn(_areaId, Document.Global);

		// Another room's area is not offered: the area is the room's key, and two rooms on one would share a thermostat.
		Areas = HouseView.Unconfigured(
			_catalog.Areas(Document.Global),
			Document.Areas.Where(other => !string.Equals(other.AreaId, _areaId, StringComparison.Ordinal)));
	}

	/// <summary>Re-derives everything that follows from the document: the validator's verdict and the period in force.</summary>
	private void Refresh()
	{
		if (Area is null)
			return;

		Validation = _engine.Validate(Document);
		Preview = _catalog.PreviewArea(Area, Defaults, Document.Global);

		AreaEntities inArea = _catalog.EntitiesInArea(Area.AreaId, Document.Global);

		HeaterChoices = Scope(inArea.Heaters, _heaters, Area.Heaters);
		SensorChoices = Scope(inArea.TemperatureSensors, _sensors, Area.TemperatureSensors);

		AreaEntities others = ScopeToArea
			? _catalog.OtherEntitiesInArea(Area.AreaId, Document.Global, labelledOnly: false)
			: AreaEntities.Empty;

		HeaterOthers = Beyond(others.Heaters, HeaterChoices);
		SensorOthers = Beyond(others.TemperatureSensors, SensorChoices);

		LowBatteryReadings = _catalog.LowBatteriesOf(Resolved?.TemperatureSensors ?? [], Document.Global);

		ReadLiveState();
	}

	/// <summary>Everything else, minus whatever the first block already offers.</summary>
	private static List<EntityOption> Beyond(IReadOnlyList<EntityOption> others, IReadOnlyList<EntityOption> offered)
	{
		HashSet<string> already = new(offered.Select(option => option.EntityId), StringComparer.Ordinal);

		return [.. others.Where(option => !already.Contains(option.EntityId))];
	}

	/// <summary>What one picker offers: this room's entities, with anything already picked from elsewhere kept in.</summary>
	private IReadOnlyList<EntityOption> Scope(
		IReadOnlyList<EntityOption> inArea,
		IReadOnlyList<EntityOption> everywhere,
		IReadOnlyList<string>? configured)
	{
		if (!ScopeToArea)
			return everywhere;

		// The filter is a default, and must never drop a chosen value.
		IEnumerable<EntityOption> outsiders = configured is null
			? []
			: everywhere.Where(option =>
				configured.Contains(option.EntityId, StringComparer.Ordinal)
				&& !inArea.Any(local => string.Equals(local.EntityId, option.EntityId, StringComparison.Ordinal)));

		return [.. inArea.Concat(outsiders).OrderBy(option => option.FriendlyName, StringComparer.CurrentCulture)];
	}

	private bool ReadLiveState()
	{
		if (Area is null)
			return false;

		string? before = ActivePeriodId;
		string still = _still;

		EntityState? thermostat = _catalog.StateOf(Slice?.Thermostat);
		EntityState? reason = _catalog.StateOf(_reasonSensor);

		Card = RoomCard.From(thermostat, reason, Now);
		_still = $"{thermostat?.LastUpdated:O}|{reason?.State}|{Verdict}";

		// The select is read only when it is allowed to decide. Under the default authority InForceNow discards
		// the value, so an unguarded read on the one-second ticker is a Home Assistant lookup per second, thrown away.
		(TimeOnly? sunrise, TimeOnly? sunset) = _catalog.SunTimesToday(_zone);
		string? selectValue = Schedule.HomeAssistantDecides(Document.Global)
			? _catalog.CurrentStateOf(Document.Global.PeriodSelect?.EntityId)
			: null;

		_activePeriod = Schedule.InForceNow(
			Periods,
			Document.Global,
			new SunTimes(sunrise, sunset),
			Now,
			selectValue,
			_zone).Period;

		return !string.Equals(ActivePeriodId, before, StringComparison.Ordinal)
			|| !string.Equals(_still, still, StringComparison.Ordinal);
	}

	// ---- the edit paths --------------------------------------------------------------------------------

	private Task Set(Action mutate)
	{
		if (Area is null)
			return Task.CompletedTask;

		mutate();

		return MarkDirty();
	}

	private Task SetList(IReadOnlyList<string> value, Action<List<string>?> assign)
	{
		if (Area is null)
			return Task.CompletedTask;

		// An empty list and a null list are different instructions: null means "discover", [] would mean "this
		// room has no heaters", which is never what clearing the last chip meant.
		assign(value.Count == 0 ? null : [.. value]);

		return MarkDirty();
	}

	/// <summary>Sets a gate's polarity, storing <c>null</c> and not <c>false</c> for the ordinary reading.</summary>
	private Task SetInvert(bool inverted, Action<bool?> assign)
	{
		if (Area is null)
			return Task.CompletedTask;

		assign(inverted ? true : null);

		return MarkDirty();
	}

	// Away, Boost, and the period rows Normal and Sleep both read, in HeatingMode order.
	private static string[] WhatEachModeReads(AreaConfig? room)
	{
		string periods = room is null
			? ""
			: string.Join(";", room.PeriodTemperatures.Select(level =>
				$"{level.PeriodId}={level.Temperature?.ToString(CultureInfo.InvariantCulture)}"));

		return
		[
			room?.AwayTemperature?.ToString(CultureInfo.InvariantCulture) ?? "",
			periods,
			periods,
			room?.BoostTemperature?.ToString(CultureInfo.InvariantCulture) ?? ""
		];
	}

	// After the document write, so a stamp never names a value the file does not hold.
	private void StampWhatMoved()
	{
		string[] now = WhatEachModeReads(Area);
		HeatingMode[] modes = [HeatingMode.Away, HeatingMode.Night, HeatingMode.Home, HeatingMode.Boost];
		List<HeatingMode> moved = [.. modes.Where((mode, index) => index >= _written.Length || now[index] != _written[index])];

		_written = now;

		if (moved.Count == 0)
			return;

		SaveOutcome stamped = _stamps.SaveModeStamps(RoomId, moved, _clock.Take());

		if (!stamped.Written)
			_logger.LogWarning("{Room} was saved and its stamps were not: {Reason}", RoomName, stamped.Message);
	}

	/// <inheritdoc/>
	public void Dispose()
	{
		try
		{
			_saveCts?.Cancel();

			if (Dirty)
				Commit();
		}
		finally
		{
			_saveCts?.Dispose();
			_ticker?.Dispose();
		}
	}
}
