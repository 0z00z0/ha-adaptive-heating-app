using System.Threading.Channels;

using Adaptive.Settings;

using AdaptiveHeating.AddOn.Driving;
using AdaptiveHeating.AddOn.Settings;
using AdaptiveHeating.Page.Settings;
using AdaptiveHeating.Planner;

using AdaptiveLighting.Engine;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using NetDaemon.HassModel;

namespace AdaptiveHeating.AddOn.Integration;

/// <summary>Keeps the add-on and the integration in touch: drives the rooms, sends what is queued, watches the clock.</summary>
/// <remarks>
///     Two things ask for a pass over the rooms: a change to an entity the heating reads, and the clock a
///     minute at a time behind it. Start-up order does not matter: a core that is
///     not up yet is a connection that is not open, and the settings the add-on holds are its own whatever the
///     other half is doing.
/// </remarks>
internal sealed class ConnectionToTheIntegration : BackgroundService
{
	/// <summary>How long a pass may be from the one before it when nothing has changed. The safety net behind the events.</summary>
	public static readonly TimeSpan PassAtLeastEvery = TimeSpan.FromMinutes(1);

	private readonly CoreConnection _connection;
	private readonly TellsTheThermostats _tells;
	private readonly DrivesTheRooms _drives;
	private readonly TheCalendarsOnOffer _offered;
	private readonly ISettingsStore _store;
	private readonly HeatingDocumentHost _document;
	private readonly TheHouseAsItIs _house;
	private readonly TheAreasOfTheHouse _areas;
	private readonly AreaDiscoveryScheduler _discovery;
	private readonly TellsEachRoomItsSettings _roomSettings;
	private readonly RoomsThatCannotBeSetUp _cannotBeSetUp;
	private readonly NetworkSetClock _clock;
	private readonly TimeProvider _time;
	private readonly ILogger<ConnectionToTheIntegration> _logger;
	private readonly PowerSensorsToPick _powerSensorsToPick;
	private readonly RoomsMovedInHomeAssistant? _movedInHomeAssistant;

	// One pass is pending at a time. Anything asking for one during a pass collapses into the single pass after
	// it, and nothing is lost by the collapse: a pass reads the whole house when it runs and carries no queue.
	private readonly Channel<byte> _wanted =
		Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

	private int _openedSeen;
	private (HeatingConfig? Document, int Opened) _sharedCheckedFor;

	public ConnectionToTheIntegration(
		CoreConnection connection,
		TellsTheThermostats tells,
		DrivesTheRooms drives,
		TheCalendarsOnOffer offered,
		ISettingsStore store,
		HeatingDocumentHost document,
		TheHouseAsItIs house,
		TheAreasOfTheHouse areas,
		AreaDiscoveryScheduler discovery,
		TellsEachRoomItsSettings roomSettings,
		RoomsThatCannotBeSetUp cannotBeSetUp,
		NetworkSetClock clock,
		TimeProvider time,
		ILogger<ConnectionToTheIntegration> logger,
		RoomsMovedInHomeAssistant? movedInHomeAssistant = null)
	{
		_movedInHomeAssistant = movedInHomeAssistant;
		_connection = connection ?? throw new ArgumentNullException(nameof(connection));
		_tells = tells ?? throw new ArgumentNullException(nameof(tells));
		_drives = drives ?? throw new ArgumentNullException(nameof(drives));
		_offered = offered ?? throw new ArgumentNullException(nameof(offered));
		_store = store ?? throw new ArgumentNullException(nameof(store));
		_document = document ?? throw new ArgumentNullException(nameof(document));
		_house = house ?? throw new ArgumentNullException(nameof(house));
		_areas = areas ?? throw new ArgumentNullException(nameof(areas));
		_discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
		_roomSettings = roomSettings ?? throw new ArgumentNullException(nameof(roomSettings));
		_cannotBeSetUp = cannotBeSetUp ?? throw new ArgumentNullException(nameof(cannotBeSetUp));
		_clock = clock ?? throw new ArgumentNullException(nameof(clock));
		_time = time ?? throw new ArgumentNullException(nameof(time));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		_powerSensorsToPick = new PowerSensorsToPick(logger);
	}

	/// <inheritdoc/>
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		Task sending = _tells.PumpAsync(stoppingToken);

		_connection.SomethingChanged += OnePassPlease;

		Task ticking = TickAsync(stoppingToken);

		OnePassPlease();

		try
		{
			while (!stoppingToken.IsCancellationRequested)
			{
				try
				{
					await _wanted.Reader.ReadAsync(stoppingToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					break;
				}

				_clock.Check();

				await OnePassAsync(stoppingToken).ConfigureAwait(false);
			}
		}
		finally
		{
			_connection.SomethingChanged -= OnePassPlease;
		}

		try
		{
			await Task.WhenAll(sending, ticking).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			// Stopping, which is what the token asked for.
		}
	}

	private async Task OnePassAsync(CancellationToken token)
	{
		// A pass before the connection opens reads an empty house, so it reports an empty cabin: no presence, no
		// outdoor sensor and no forecast, none of which it can see yet. Opening the connection asks for a pass of
		// its own, and the pass a minute is behind that.
		if (!_connection.IsOpen)
		{
			_logger.LogDebug("No pass over the rooms: the connection to Home Assistant is not open yet.");

			return;
		}

		// Counted rather than watched for: a connection that went and came back between two passes leaves no
		// transition to see, and a core restart clears every card it was showing. What still holds is said again
		// on the first pass over a connection this loop has not seen before.
		int opened = _connection.TimesOpened;

		if (_openedSeen != opened)
		{
			_openedSeen = opened;
			_drives.TheConnectionCameBack();
			_roomSettings.TheConnectionCameBack();
		}

		IReadOnlyList<EntityState> states = _connection.House;

		// The settings page chooses a calendar out of what Home Assistant holds, and which integration created
		// each one is what says whether the record may be written to it.
		_store.ReceiveTheCalendarsOffered(_offered.Offered(states));

		await TheRoomsAsync(opened, states, token).ConfigureAwait(false);

		// After the rooms are known, so a room this pass learnt about is driven on the same pass.
		await _drives.RunAsync(states, _time.GetUtcNow(), token).ConfigureAwait(false);
	}

	/// <summary>The rooms the Areas hold: discover them where there are none, then resolve and send each one.</summary>
	// Nothing happens while the entity model is down: an Area read off an unfilled cache holds no heater, and
	// every room would be skipped for that.
	private async Task TheRoomsAsync(int opened, IReadOnlyList<EntityState> states, CancellationToken token)
	{
		if (_house.Context is not { } context)
			return;

		HeatingConfig document = _document.Current;

		AreaEntityResolver resolver = new(
			context, _areas, document.Global, _areas, HeatingActions.Domain, NullLogger<AreaEntityResolver>.Instance);

		if (_discovery.ScanIfNeeded(opened, _areas, resolver, context).Count > 0)
			document = _document.Current;

		// Before the merge, which would hand the moved thermostat's old area to whatever is in it now.
		if (_movedInHomeAssistant is not null)
			await _movedInHomeAssistant.FollowAsync(states, _time.GetUtcNow(), token).ConfigureAwait(false);

		MergedRooms merged = RoomsFromTheAreas.MergeInto(_store, _document, states, _areas, resolver);
		document = _document.Current;

		if (merged.Changed)
			_logger.LogInformation("The Areas hold {Count} room(s).", merged.Document.Rooms.Count);

		// Before the rooms: a room the House tab's label finds has no thermostat until the integration knows it.
		await _roomSettings.SendLabelsAsync(document.Global, token).ConfigureAwait(false);

		await _roomSettings
			.SendWhatChangedAsync(RoomsFromTheAreas.Regulation(_store.Read(), document, resolver), token)
			.ConfigureAwait(false);

		_powerSensorsToPick.Check(document, resolver);

		await _cannotBeSetUp.ReportAsync(document, _store.Read(), resolver, token).ConfigureAwait(false);
		await _cannotBeSetUp.ReportTheFileAsync(_document, opened, token).ConfigureAwait(false);

		ReportSharedHeaters(opened, document, context, resolver);
	}

	/// <summary>Finds the heaters two rooms both command, and says so once for each, as lighting does for its lights.</summary>
	// Once per document and connection: a failure here costs the advice and never the heating.
	private void ReportSharedHeaters(int opened, HeatingConfig document, IHaContext context, AreaEntityResolver resolver)
	{
		if (ReferenceEquals(document, _sharedCheckedFor.Document) && opened == _sharedCheckedFor.Opened)
			return;

		_sharedCheckedFor = (document, opened);

		IReadOnlyList<SuspectLight> shared;

		try
		{
			List<RoomUnderReview> rooms = [];

			foreach (AreaConfig area in document.Areas)
			{
				if (!resolver.TryResolve(area, document.Defaults, out ResolvedRoom? room, out _))
					continue;

				rooms.Add(new RoomUnderReview(
					AreaNaming.DisplayName(area, _areas),
					[.. room.Heaters.Select(heater => new LightUnderReview(heater, context.AttrString(heater, "friendly_name") ?? heater))]));
			}

			// A heater named by two rooms is commanded by both, whatever area Home Assistant files it under.
			shared = LightAudit.SharedBetweenRooms(rooms, _ => false);
		}
		catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
		{
			_logger.LogDebug(exception, "Could not check whether any heater is commanded by two rooms at once.");
			return;
		}

		_document.FoundSharedLights(shared);

		foreach (SuspectLight heater in shared)
			_logger.LogWarning(
				"Heater '{Heater}' ({EntityId}) is {Reason}. Until then both rooms set its temperature, and whichever "
				+ "asks last wins.",
				heater.Name, heater.EntityId, heater.Reason);
	}

	private async Task TickAsync(CancellationToken token)
	{
		while (!token.IsCancellationRequested)
		{
			try
			{
				await Task.Delay(PassAtLeastEvery, _time, token).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return;
			}

			OnePassPlease();
		}
	}

	/// <summary>Asks for one pass over the rooms. Collapses into the pass already pending.</summary>
	public void OnePassPlease() => _wanted.Writer.TryWrite(0);
}
