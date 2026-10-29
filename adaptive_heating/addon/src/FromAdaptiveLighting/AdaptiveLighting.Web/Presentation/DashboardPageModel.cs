using AdaptiveHeating.Page.Settings;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Web.Presentation;

/// <summary>A room as the grid draws it; Target is null before it has reported one.</summary>
public sealed record RoomTile(string Name, string? AreaId, double? Target, bool IsHandHeld);

/// <summary>Everything the dashboard holds, decides and says; it never writes the document.</summary>
public sealed class DashboardPageModel : IPageClock, IDisposable
{
	/// <summary>How long the house-mode acknowledgement stays on screen.</summary>
	private static readonly TimeSpan ModeConfirmationLingers = TimeSpan.FromSeconds(6);

	private readonly AreaSnapshotCache _cache;
	private readonly IActivityRecord _log;
	private readonly HeatingDocumentHost _engine;
	private readonly ModeService _service;
	private readonly HaCatalog _catalog;
	private readonly TimeZoneInfo _zone;
	private readonly ILogger _logger;
	private readonly Func<Action, Task> _dispatch;

	private IReadOnlyList<AreaSnapshot> _snapshots = [];
	private IReadOnlyList<RoomView> _rooms = [];
	private IReadOnlyList<RoomTile> _roomTiles = [];
	private IReadOnlyList<RecordedReport> _entries = [];

	// _kept is what the summary categories leave; _reachable is what the Activity page would draw.
	private int _kept;
	private int _reachable;

	private TransientMessage _modeConfirmation = TransientMessage.None;

	private IDisposable? _subscription;
	private IDisposable? _ticker;

	// dispatch runs work on the thread the interface renders on; a test runs it inline.
	public DashboardPageModel(
		AreaSnapshotCache cache,
		IActivityRecord log,
		HeatingDocumentHost engine,
		ModeService service,
		HaCatalog catalog,
		TimeZoneInfo zone,
		ILogger logger,
		Func<Action, Task> dispatch)
	{
		_cache = cache ?? throw new ArgumentNullException(nameof(cache));
		_log = log ?? throw new ArgumentNullException(nameof(log));
		_engine = engine ?? throw new ArgumentNullException(nameof(engine));
		_service = service ?? throw new ArgumentNullException(nameof(service));
		_catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
		_zone = zone ?? throw new ArgumentNullException(nameof(zone));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		_dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
	}

	/// <summary>Raised when a value the house bar shows has moved; the clock alone raises <see cref="Tick"/>.</summary>
	public event Action? Changed;

	/// <inheritdoc/>
	public event Action? Tick;

	/// <inheritdoc/>
	public DateTimeOffset Now { get; private set; } = DateTimeOffset.Now;

	/// <summary>Reads the house, builds the board, and starts the report subscription and the clock.</summary>
	public void Start()
	{
		_snapshots = _cache.Snapshots;
		ReadHouseState();
		ReadBoard();

		// Sampled, not raw: a house waking up pushes a burst of transitions.
		_subscription = _cache.Changes
			.Sample(TimeSpan.FromMilliseconds(400))
			.SubscribeSafe(report => OnReport(), _logger);

		// The now-line and every countdown are only honest while they move, so the board is rebuilt every second.
		_ticker = Observable.Interval(TimeSpan.FromSeconds(1))
			.SubscribeSafe(beat => Beat(), _logger);
	}

	private void OnReport() => _ = _dispatch(() =>
	{
		_snapshots = _cache.Snapshots;
		ReadHouseState();
		ReadBoard();
		Changed?.Invoke();
	});

	private void Beat() => _ = _dispatch(() =>
	{
		Now = DateTimeOffset.Now;

		HouseBarState before = HouseBar;

		ReadHouseState();
		ReadBoard();

		if (HouseBar != before)
			Changed?.Invoke();

		Tick?.Invoke();
	});

	// ---- the house bar ---------------------------------------------------------------------------------

	public HouseModeView? HouseMode { get; private set; }

	public IReadOnlyList<PersonView> People { get; private set; } = [];

	/// <summary>Why a mode could not be switched, which stands until the next press.</summary>
	public string? ModeMessage { get; private set; }

	/// <summary>The acknowledgement of a mode press, read against <see cref="Now"/> where it is drawn.</summary>
	public TransientMessage ModeConfirmation => _modeConfirmation;

	/// <summary>Switches the house mode, then re-reads.</summary>
	/// <remarks>The call is fire and forget; the current-mode highlight comes from what Home Assistant reports.</remarks>
	public void SelectMode(string option)
	{
		Now = DateTimeOffset.Now;

		if (_service.SelectHouseMode(option))
		{
			ModeMessage = null;
			_modeConfirmation = TransientMessage.For($"Switching to {option}…", Now, ModeConfirmationLingers);
		}
		else
		{
			_modeConfirmation = TransientMessage.None;
			ModeMessage = $"Couldn't switch to '{option}' — Home Assistant offline?";
		}

		ReadHouseState();
	}

	// ---- what the page is looking at -------------------------------------------------------------------

	/// <summary>Whether any room reports the master switch as holding everything.</summary>
	public bool KillSwitchActive => _snapshots.Any(area => area.KillSwitchActive);

	public bool EngineIsRunning => _engine.Fault is null;

	public string? EngineFault => _engine.Fault;

	/// <summary>Whether any room has reported at all.</summary>
	public bool HasData => _cache.HasData;

	/// <summary>Whether the house has rooms but none has been switched on yet, with Home Assistant answering.</summary>
	public bool AwaitingRoomChoice =>
		AreaView.IsAwaitingRoomChoice(_engine.Current.Areas, _engine.Current.Defaults, _catalog.IsHomeAssistantResponding(_engine.Current.Global));

	/// <summary>The summary log's rows.</summary>
	public IReadOnlyList<ActivityRow> LogRows { get; private set; } = [];

	// ---- the timeline ----------------------------------------------------------------------------------

	public IReadOnlyList<BoardLane> Lanes { get; private set; } = [];

	/// <summary>The lanes grouped by floor, or one unnamed group where the registry cannot say.</summary>
	public IReadOnlyList<FloorGroup<BoardLane>> Groups { get; private set; } = [];

	/// <summary>The lanes that have done nothing inside the window.</summary>
	public IReadOnlyCollection<string> Quiet => _quiet;

	public IReadOnlyList<AreaSnapshot> Exceptions { get; private set; } = [];

	public IReadOnlyList<BandSegment> Band { get; private set; } = [];

	public BoardWindow Window { get; private set; } =
		BoardWindow.Around(DateTimeOffset.Now, BoardView.LookBack, BoardView.LookAhead);

	public IReadOnlyList<DateTimeOffset> Ticks { get; private set; } = [];

	/// <summary>How far across the window the present moment is, from 0 to 100.</summary>
	public double NowPercent { get; private set; }

	/// <summary>Whether any lane carries a mark, which is what the legend explains.</summary>
	public bool HasMarks { get; private set; }

	private HashSet<string> _quiet = new(StringComparer.Ordinal);

	/// <summary>Whether a lane is resting rather than drawn on the board.</summary>
	public bool IsQuiet(BoardLane lane) => _quiet.Contains(lane.Key);

	public bool ShowsFloorHeader(FloorGroup<BoardLane> group) => AreaView.ShowsHeader(Groups.Count, group.Floor);

	public static string FloorTitle(AreaFloor? floor) => AreaView.FloorTitle(floor);

	/// <summary>Whether a lane's room has a temperature sensor with a low battery.</summary>
	public static bool HasWarning(AreaSnapshot snapshot) => snapshot.LowBatteries is { Count: > 0 };

	public static bool IsLabelled(BandSegment segment) => BoardView.IsLabelled(segment);

	// ---- the sentences ---------------------------------------------------------------------------------

	public string ExceptionLine(AreaSnapshot area) => BoardView.ExceptionLine(area, Now);

	/// <summary>How many rooms are quiet, said in words.</summary>
	public string QuietRoomsLine => BoardView.QuietRoomsLine(Lanes.Count, Exceptions.Count);

	public string NowClock => BoardView.Clock(Now);

	/// <summary>How far back the board can see: to the first report this run heard.</summary>
	public string SinceLine =>
		_cache.Since is { } since
			? $"since {BoardView.Clock(since)}"
			: "since start";

	/// <summary>What the log below is showing, what the filter is holding back, and the cap it is held to.</summary>
	public string LogFoot => BoardView.LogFoot(_entries.Count, _reachable, _kept, LogRows.Count, _log.Capacity);

	/// <summary>What a lane says on hover: the room, its state, its temperature, and low batteries.</summary>
	public string LaneTitle(BoardLane lane)
	{
		string word = StateGlyph.For(lane.Latest).Word;
		string title = lane.Latest.Target is { } target
			? $"{lane.Name} — {word} — {target.ToString("0.#", CultureInfo.CurrentCulture)} °C"
			: $"{lane.Name} — {word}";

		foreach (string battery in RoomFacts.LowBatteries(lane.Latest.LowBatteries ?? [], _catalog.FriendlyNameOrId))
			title = $"{title} — {battery}";

		return title;
	}

	/// <summary>What the page says about rooms that are switched off, when it says anything.</summary>
	public HiddenRoomsNote? HiddenRooms => AreaView.HiddenNote(AreaView.SwitchedOffCount(_rooms));

	/// <summary>Every switched-on room, with the temperature it is held at; a room that has not reported is dark,
	/// not missing.</summary>
	public IReadOnlyList<RoomTile> Rooms => _roomTiles;

	// ---- reading ---------------------------------------------------------------------------------------

	/// <summary>Re-reads the page the moment the first-run board's save lands, ahead of the next beat.</summary>
	public void OnCommissioned()
	{
		ReadHouseState();
		ReadBoard();
	}

	private void ReadHouseState()
	{
		HouseMode = _service.GetHouseMode();
		People = _service.GetPeople();
	}

	/// <summary>Everything the house bar draws, so it is redrawn only when one of them has moved.</summary>
	private HouseBarState HouseBar =>
		new(HouseMode, People.Count, ModeMessage, _modeConfirmation.TextAt(Now));

	private readonly record struct HouseBarState(
		HouseModeView? HouseMode,
		int PeopleCount,
		string? ModeMessage,
		string? ModeConfirmation);

	// Rebuilt on every beat and never cached: the window steps on the hour and every countdown goes wrong once it
	// stops being recalculated.
	private void ReadBoard()
	{
		_rooms = _service.GetRooms();
		_roomTiles =
		[
			.. _rooms.Where(room => room.IsEnabled)
				.Select(room => _cache.Find(room.AreaId, room.Name) is { } snapshot
					? new RoomTile(room.Name, room.AreaId, snapshot.Target, snapshot.State is AreaState.OverriddenOn)
					: new RoomTile(room.Name, room.AreaId, null, false))
		];
		Window = BoardWindow.Around(Now, BoardView.LookBack, BoardView.LookAhead);
		Ticks = Window.Ticks;
		NowPercent = Math.Clamp(Window.PercentAt(Now), 0, 100);
		_entries = _log.Read().Reports;

		IReadOnlyList<RecordedReport> kept = ActivityView.InCategories(_entries, ActivityView.SummaryCategories);

		// DefaultCategories, because that is what the Activity page opens on.
		_reachable = ActivityView.InCategories(_entries, ActivityView.DefaultCategories).Count;
		_kept = kept.Count;
		LogRows = ActivityView.Rows(kept, BoardView.LogPreview);

		IReadOnlyList<AreaSnapshot> visible = AreaView.VisibleCards(_snapshots, _rooms);

		Dictionary<string, List<AreaSnapshot>> byRoom = new(StringComparer.Ordinal);
		foreach (AreaSnapshot snapshot in _cache.History)
		{
			if (!byRoom.TryGetValue(AreaSnapshotCache.KeyOf(snapshot), out List<AreaSnapshot>? room))
				byRoom[AreaSnapshotCache.KeyOf(snapshot)] = room = [];

			room.Add(snapshot);
		}

		List<BoardLane> lanes = new(visible.Count);
		foreach (AreaSnapshot snapshot in visible)
		{
			string key = AreaSnapshotCache.KeyOf(snapshot);
			IReadOnlyList<AreaSnapshot> history = byRoom.TryGetValue(key, out List<AreaSnapshot>? found) ? found : [];

			lanes.Add(new BoardLane(
				key,
				snapshot.AreaName,
				snapshot.AreaId,
				snapshot,
				BoardView.Blocks(history, Window, Now),
				BoardView.NextMark(snapshot, Window, Now)));
		}

		Lanes = lanes;
		HasMarks = lanes.Any(lane => lane.Blocks.Count > 0 || lane.Next is not null);
		Exceptions = BoardView.Exceptions(visible);

		(IReadOnlyList<BoardLane> _, IReadOnlyList<BoardLane> quiet) = BoardView.Partition(lanes);
		_quiet = new HashSet<string>(quiet.Select(lane => lane.Key), StringComparer.Ordinal);

		// A registry that has not connected throws instead of answering, so the board renders flat.
		Groups = _catalog.AreaRegistry is { } registry
			? FloorGrouping.GroupOrFlat(lanes, lane => lane.AreaId, registry)
			: lanes.Count == 0 ? [] : [new FloorGroup<BoardLane>(null, lanes)];

		ReadSchedule();
	}

	/// <summary>Re-reads the period table for the band above the lanes, as the engine's own table places it.</summary>
	private void ReadSchedule()
	{
		HeatingConfig schedule = _engine.Current;

		SunTimes sun;
		try
		{
			(TimeOnly? sunrise, TimeOnly? sunset) = _catalog.SunTimesToday(_zone, schedule.Global.SunEntity);
			sun = new SunTimes(sunrise, sunset);
		}
		catch (InvalidOperationException)
		{
			sun = SunTimes.Unknown;
		}

		// Read only where it is allowed to decide: otherwise it is a Home Assistant lookup per second per open board.
		string? selectValue = Schedule.HomeAssistantDecides(schedule.Global)
			? _catalog.CurrentStateOf(schedule.Global.PeriodSelect?.EntityId)
			: null;

		Band = BoardView.Band(Schedule.CalculatorFor(schedule.Periods, schedule.Global, sun, selectValue, _zone), Window);
	}

	/// <inheritdoc/>
	public void Dispose()
	{
		_subscription?.Dispose();
		_ticker?.Dispose();
	}
}
