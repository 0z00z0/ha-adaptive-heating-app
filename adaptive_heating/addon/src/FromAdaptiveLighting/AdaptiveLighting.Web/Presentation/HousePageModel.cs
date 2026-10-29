using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Web.Components;

namespace Adaptive.UI;

/// <summary>Everything the house page knows and does: its state, its document, its edits, its save and every
/// sentence it shows.</summary>
/// <remarks>A plain class, not a component: a second design puts its own markup over this same model, so a rule
/// left in a razor file is a rule that design writes again. The interface keeps markup, styling and the wiring
/// of events to these methods, and nothing else. No member here names a stylesheet class or a custom
/// property.</remarks>
public sealed class HousePageModel : IPageClock, IDisposable
{
	/// <summary>How long a success confirmation stays on screen.</summary>
	private static readonly TimeSpan ConfirmationLife = TimeSpan.FromSeconds(3.5);

	// SentenceView has no default for this: only the page knows whether a pick waits behind a save bar.
	public const string TokenNote = "Applies when you save. Discard changes puts it back.";

	/// <summary>What the browser asks before leaving with an unsaved edit.</summary>
	public const string LeaveQuestion = "Leave this page? Changes that are not saved will be lost.";

	private readonly HeatingDocumentHost _engine;
	private readonly HaCatalog _catalog;
	private readonly ModeService _modes;
	private readonly ILogger _logger;

	private HeatingConfig _config = new();

	// The whole document as this page read it. This page edits a whole draft, so what it must not overwrite is
	// the whole document.
	private string _documentStamp = "";

	// Compared against a serialised snapshot, never set by a flag each edit path remembers: a dozen paths change
	// this document, and one that forgot the flag would leave an edit with no way to save it.
	private bool _dirty;
	private string? _cleanDocument;

	// The confirmation and the beat that clears it. The beat runs only while there is something to clear: this
	// page has nothing else that moves on its own, so a standing clock would redraw for nothing.
	private TransientMessage _confirmation = TransientMessage.None;
	private IDisposable? _clock;

	// Starts as "cannot compare", which is what a page that has read nothing yet knows.
	private HouseModeOptionsDiff _helperDiff = new(false, [], []);

	private EntityLookup? _lookup;

	// Each room's slot as the file held it when this page last read it, keyed by area id. The scoped write that
	// the room's switch makes is checked against this rather than against the draft.
	private readonly Dictionary<string, string> _roomStamps = new(StringComparer.Ordinal);

	private readonly ISettingsStore _rooms;

	// Absent where the host resolves no location, which then has no warning to give.
	private readonly ConfigLocation? _location;

	private IReadOnlyList<AreaOption> _areas = [];

	// Built with the floor groups, never per render: the registry throws until Home Assistant has connected.
	private readonly Dictionary<AreaConfig, string> _roomNames = [];

	private IReadOnlyDictionary<AreaConfig, IReadOnlyList<string>> _lowBatteries =
		new Dictionary<AreaConfig, IReadOnlyList<string>>();

	// _unsaved holds rooms added since the last save. They cannot be opened: the room page reads the document
	// from disk and would find no such room.
	private readonly HashSet<string> _unsaved = new(StringComparer.Ordinal);

	/// <param name="rooms">What each room is doing and has learnt, for the hand-held mark on its row.</param>
	public HousePageModel(
		HeatingDocumentHost engine,
		HaCatalog catalog,
		ModeService modes,
		ILogger<HousePageModel> logger,
		ISettingsStore rooms,
		ConfigLocation? location = null)
	{
		_location = location;
		_engine = engine ?? throw new ArgumentNullException(nameof(engine));
		_catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
		_modes = modes ?? throw new ArgumentNullException(nameof(modes));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		_rooms = rooms ?? throw new ArgumentNullException(nameof(rooms));
	}

	/// <summary>Raised whenever anything the interface draws has moved.</summary>
	/// <remarks>Raised from whatever thread did the moving, so a design marshals it itself.</remarks>
	public event Action? Changed;

	/// <inheritdoc/>
	public event Action? Tick;

	/// <inheritdoc/>
	public DateTimeOffset Now { get; private set; } = DateTimeOffset.Now;

	/// <summary>Reads the document.</summary>
	public void Start() => Reload();

	// ---- the document, for the shared controls that draw configuration objects ----

	/// <summary>The draft this page edits. Handed to the controls that draw a configuration object; every change
	/// to it goes through a method here.</summary>
	public HeatingConfig Config => _config;

	public GlobalConfig Global => _config.Global;

	public List<TimePeriodConfig> Periods => _config.Periods;

	public HouseModeConfig? HouseMode => _config.Global.HouseMode;

	// ---- load, save and what the save bar says ----

	public string? LoadError { get; private set; }

	public ValidationResult? Validation { get; private set; }

	public SaveResult? Result { get; private set; }

	/// <summary>The success confirmation and the moment it stops being made.</summary>
	/// <remarks>Read against <see cref="Now"/> where it is drawn, so it clears itself with no timer behind it.</remarks>
	public TransientMessage SaveConfirmation => _confirmation;

	public bool IsBusy { get; private set; }

	/// <summary>Whether the save bar has anything to say: an unsaved edit, an unresolved refusal, or a
	/// confirmation still on screen.</summary>
	public bool ShowSaveBar =>
		_dirty
		|| _confirmation.IsShownAt(Now)
		|| Result is { Written: false };

	public bool HasUnsavedEdits => _dirty;

	/// <summary>How many problems the save bar reports, or <c>null</c> when it reports none.</summary>
	public string? SaveBarProblems => Validation is { IsValid: false } validation
		? $"{validation.Errors.Count} {(validation.Errors.Count == 1 ? "problem" : "problems")} to fix before saving."
		: null;

	/// <summary>Whether the validator has anything at all to show above the section rail.</summary>
	public bool HasValidationNotice => Validation is { } validation
		&& (validation.Errors.Count > 0 || validation.AreaErrors.Count > 0 || validation.Warnings.Count > 0);

	public void Reload()
	{
		Result = null;
		CloseSetup();
		CloseAdd();
		_unsaved.Clear();
		SetupNote = null;

		try
		{
			_config = _engine.Store.Load();
			_documentStamp = ConfigStamp.OfDocument(_config);

			// Taken here, where the draft still is the file, so no edit made later can reach the anchors.
			AnchorRoomStamps(_config);
			LoadError = null;
		}
		catch (HeatingConfigException exception)
		{
			LoadError = exception.Message;
			Notify();

			return;
		}

		LoadCatalog();
		Revalidate();
		MarkClean();
		Notify();
	}

	public void StartFromScratch()
	{
		_config = new HeatingConfig();

		// Nothing was read, so there is nothing this draft could revert. Only reachable over a file that would
		// not parse, and this save is the way out of that.
		_documentStamp = "";
		_roomStamps.Clear();
		LoadError = null;
		LoadCatalog();
		Revalidate();
	}

	public void Save()
	{
		IsBusy = true;

		// Drop any lingering success toast, so a refused save is never shown next to a stale confirmation.
		ClearConfirmation();

		try
		{
			// Read before the save, so the confirmation below can check the file's timestamp actually advanced.
			DateTimeOffset? writtenBefore = _engine.Store.LastWrittenUtc;

			// The host refuses it when the file has moved on since it was read: this page sends the whole
			// document, so that file would be overwritten and not edited.
			Result = DocumentWrite.Save(_engine, _documentStamp, _config);
			Validation = Result.Validation;

			if (Result.Written)
			{
				DateTimeOffset? writtenAfter = _engine.Store.LastWrittenUtc;
				string savedAt = SavedAt();
				bool advanced = writtenAfter is { } after && (writtenBefore is not { } before || after >= before);

				// Result is cleared so the persistent note never restates the toast. A refused save keeps it.
				ShowSaveConfirmation(advanced
					? $"Saved ✓ {savedAt}"
					: "Saved, timestamp unchanged — check the log");
				Result = null;

				// Everything on disk now has a page to open.
				_unsaved.Clear();
				SetupNote = null;

				// Re-read so the page shows what is on disk, formatting and all.
				_config = _engine.Store.Load();
				_documentStamp = ConfigStamp.OfDocument(_config);
				AnchorRoomStamps(_config);
				LoadCatalog();
				Revalidate();
				MarkClean();
			}
		}
		catch (HeatingConfigException exception)
		{
			LoadError = exception.Message;
		}
		finally
		{
			IsBusy = false;
			Notify();
		}
	}

	/// <summary>Shows the success confirmation and starts the beat that clears it.</summary>
	/// <remarks>The first beat falls exactly where the confirmation runs out, so it stays up for the same time
	/// however long the save took. A later save restarts the beat, so rapid saves cannot clear one another.</remarks>
	private void ShowSaveConfirmation(string message)
	{
		Now = DateTimeOffset.Now;
		_confirmation = TransientMessage.For(message, Now, ConfirmationLife);

		_clock?.Dispose();
		_clock = Observable.Timer(ConfirmationLife, TimeSpan.FromSeconds(1))
			.SubscribeSafe(_ => Beat(), _logger);
	}

	private void Beat()
	{
		Now = DateTimeOffset.Now;

		// Nothing else on this page moves on its own, so the beat stops with the message it was started for.
		if (!_confirmation.IsShownAt(Now))
		{
			_confirmation = TransientMessage.None;
			_clock?.Dispose();
			_clock = null;
			Notify();
		}

		Tick?.Invoke();
	}

	private void ClearConfirmation()
	{
		_confirmation = TransientMessage.None;
		_clock?.Dispose();
		_clock = null;
	}

	/// <inheritdoc/>
	public void Dispose() => ClearConfirmation();

	private void Notify() => Changed?.Invoke();

	/// <summary>Records the document as matching the file, after a load and after a save that wrote.</summary>
	private void MarkClean()
	{
		_cleanDocument = Snapshot();
		_dirty = false;
	}

	/// <summary>The document as text, or <c>null</c> when it cannot be serialised.</summary>
	/// <remarks>Null counts as changed, so the bar shows and the save reports the real error.</remarks>
	private string? Snapshot()
	{
		try
		{
			return HeatingConfigDocument.Serialize(_config);
		}
		catch (HeatingConfigException)
		{
			return null;
		}
	}

	/// <summary>Takes the document's measure again after an edit: dirty, valid, and every sentence built from
	/// it.</summary>
	public void Revalidate()
	{
		string? current = Snapshot();
		_dirty = current is null || _cleanDocument is null || !string.Equals(current, _cleanDocument, StringComparison.Ordinal);

		Validation = _engine.Validate(_config);
		AwaySentence = [HouseSentences.AwayDebounce(_config.Global)];
		ModeLines = HouseSentences.Modes(_config.Global.HouseMode, _config.Periods);

		// Recomputed here, not at each place that could move it: they all end in a Revalidate, and a comparison
		// refreshed at only some of them outlives what it was calling for.
		_helperDiff = HouseModeSync.Compare(_config.Global.HouseMode, LiveSelectOptions);
		AddableAreas = HouseView.Unconfigured(_areas, _config.Areas);
		RegroupAreas();
		RefreshLiveStates();
		Notify();
	}

	private void LoadCatalog()
	{
		// Areas are labelled with what discovery resolves in each, so the conventions come from the document
		// being edited, not from a default.
		_areas = _catalog.Areas(_config.Global);
		InputSelects = _catalog.EntitiesInDomains("input_select");
		DateTimes = _catalog.DateTimeEntities();
		ActivationEntities = _catalog.EntitiesInDomains("input_boolean", "switch", "binary_sensor");
		LiveSelectOptions = _catalog.SelectOptionsOf(_config.Global.HouseMode?.Entity);
		ActiveModeValue = _catalog.CurrentStateOf(_config.Global.HouseMode?.Entity);
		HouseModeOptionValues = ModeOptionValues();
		LivePeriodSelectOptions = _catalog.SelectOptionsOf(_config.Global.PeriodSelect?.EntityId);
		ActivePeriodValue = _catalog.CurrentStateOf(_config.Global.PeriodSelect?.EntityId);
		HomeAssistantIsAnswering = _catalog.IsHomeAssistantResponding(_config.Global);

		// The presence-reset override picker: person entities.
		PresenceEntities = _catalog.EntitiesInDomains("person");

		LabelOptions = _catalog.LabelOptions();
		SunEntities = _catalog.EntitiesInDomains("sun");
		PersonOptions = _catalog.EntitiesInDomains("person", "device_tracker");
		Switches = _catalog.EntitiesInDomains("input_boolean", "switch");
		OutdoorSensors = _catalog.EntitiesWithDeviceClass("sensor", ["temperature"]);
		Forecasts = _catalog.EntitiesInDomains("weather");
		MasterSwitch = _modes.GetMasterSwitch();
		People = _modes.GetPeople();
	}

	// ---- what the pickers are drawn from ----

	public bool HomeAssistantIsAnswering { get; private set; }

	public IReadOnlyList<EntityOption> InputSelects { get; private set; } = [];

	public IReadOnlyList<EntityOption> PresenceEntities { get; private set; } = [];

	public IReadOnlyList<EntityOption> DateTimes { get; private set; } = [];

	public IReadOnlyList<EntityOption> ActivationEntities { get; private set; } = [];

	public IReadOnlyList<EntityOption> SunEntities { get; private set; } = [];

	public IReadOnlyList<EntityOption> PersonOptions { get; private set; } = [];

	public IReadOnlyList<EntityOption> Switches { get; private set; } = [];

	public IReadOnlyList<EntityOption> OutdoorSensors { get; private set; } = [];

	public IReadOnlyList<EntityOption> Forecasts { get; private set; } = [];

	public IReadOnlyList<LabelOption> LabelOptions { get; private set; } = [];

	/// <summary>What every picker on this page is told about an id it did not offer.</summary>
	// Built once: a fresh pair each render would hand every picker a changed parameter on every tick.
	public EntityLookup Lookup => _lookup ??= new EntityLookup(_catalog.FriendlyNameOrId, _catalog.Knows);

	/// <summary>The empty-picker note: this page knows why a list is empty and the component does not, so a lost
	/// connection is not reported as an empty domain.</summary>
	public string EmptyNote(string noneYet) => HomeAssistantIsAnswering
		? noneYet
		: "Waiting for Home Assistant — type an id";

	public IReadOnlyList<AreaOption> AddableAreas { get; private set; } = [];

	/// <summary>Why the add-a-room picker has nothing to offer: no connection, or no area left unclaimed.</summary>
	public string AddEmptyNote => !HomeAssistantIsAnswering
		? "Waiting for Home Assistant — type an area id"
		: "Every area has a room — type an id";

	// ---- the rooms list ----

	public IReadOnlyList<FloorGroup<AreaConfig>> AreaGroups { get; private set; } = [];

	public IReadOnlyList<AreaConfig> Areas => _config.Areas;

	public bool HasNoRooms => _config.Areas.Count == 0;

	/// <summary>Groups the rooms by floor through the helper the board shares, so the two pages cannot
	/// disagree.</summary>
	/// <remarks>A registry that has not connected throws instead of answering, so GroupOrFlat degrades to one
	/// unnamed group and the list renders flat instead of not at all.</remarks>
	private void RegroupAreas()
	{
		AreaGroups = _catalog.AreaRegistry is { } registry
			? FloorGrouping.GroupOrFlat(_config.Areas, area => area.AreaId, registry)
			: _config.Areas.Count == 0 ? [] : [new FloorGroup<AreaConfig>(null, [.. _config.Areas])];

		// Names come from the same registry read as the floors, and go stale at the same moment: the next edit.
		_roomNames.Clear();

		foreach (AreaConfig area in _config.Areas)
			_roomNames[area] = HouseView.DisplayName(area, _catalog.AreaRegistry);
	}

	/// <summary>Each room's low sensor batteries, read with the room names they are shown under.</summary>
	private void RefreshLiveStates()
	{
		Dictionary<AreaConfig, IReadOnlyList<string>> batteries = [];

		foreach (AreaConfig area in _config.Areas)
		{
			if (_catalog.PreviewArea(area, _config.Defaults, _config.Global).Resolved is not { } resolved)
				continue;

			if (RoomFacts.LowBatteries(_catalog.LowBatteriesOf(resolved.TemperatureSensors, _config.Global), _catalog.FriendlyNameOrId) is { Count: > 0 } low)
				batteries[area] = low;
		}

		_lowBatteries = batteries;
		MasterSwitch = _modes.GetMasterSwitch();
	}

	/// <summary>What a room's row says about low sensor batteries, or <c>null</c> while none is low.</summary>
	public string? LowBatteryTitle(AreaConfig area) =>
		_lowBatteries.TryGetValue(area, out IReadOnlyList<string>? low) ? string.Join(" ", low) : null;

	/// <summary>Whether a room stands on a temperature set at its thermostat, which the mode does not correct until the hold runs out.</summary>
	public bool IsHeldByHand(AreaConfig area) =>
		area.AreaId is { Length: > 0 } areaId
		&& _rooms.Read().SetByHand.Any(room => string.Equals(room.AreaId, areaId, StringComparison.Ordinal));

	/// <summary>Whether a room's heaters are managed, the house's answer standing in where the room has none.</summary>
	public bool IsEnabled(AreaConfig area) => AreaView.IsEnabled(area, _config.Defaults);

	/// <summary>A room's own page, or <c>null</c> when there is nothing to open yet.</summary>
	/// <remarks>A room with no area has no route, and one added since the last save has none yet: the room page
	/// loads from disk, so the link would land on "no such room".</remarks>
	public string? RoomLink(AreaConfig area) =>
		area.AreaId is { Length: > 0 } areaId && !_unsaved.Contains(areaId) ? HouseView.RoomHref(areaId) : null;

	/// <summary>What a room is called in this list, matching the board, the log and the room's own page.</summary>
	/// <remarks>Read from the map built with the document and never per render. A room added since the last
	/// rebuild falls back to the document alone, one render behind.</remarks>
	public string RoomName(AreaConfig area) =>
		_roomNames.TryGetValue(area, out string? name) ? name : HouseView.DisplayName(area, null);

	/// <summary>The one-line summary of what a room is made of.</summary>
	public static string RoomSummary(AreaConfig area) => HouseView.RoomSummary(area);

	/// <summary>What the room's own switch says it does, for a pointer.</summary>
	public static string ToggleTitle(bool enabled) => enabled
		? "On — the heaters in this room are managed. Switch off to leave them alone."
		: "Off — this room is watched but its heaters are never changed.";

	/// <summary>What a screen reader calls the room's switch.</summary>
	public string ToggleLabel(AreaConfig area) => $"Adaptive Heating in {RoomName(area)}";

	/// <summary>The line naming how many rooms are switched off, or <c>null</c> when none are.</summary>
	public string? SwitchedOffLine => HouseView.SwitchedOffLine(_config.Areas, _config.Defaults) is { Length: > 0 } line
		? line
		: null;

	/// <summary>Whether a floor gets a heading of its own: only where there is more than one floor to tell
	/// apart.</summary>
	public bool ShowsFloorHeader(FloorGroup<AreaConfig> floor)
	{
		ArgumentNullException.ThrowIfNull(floor);

		return AreaView.ShowsHeader(AreaGroups.Count, floor.Floor);
	}

	public static string FloorTitle(FloorGroup<AreaConfig> floor)
	{
		ArgumentNullException.ThrowIfNull(floor);

		return AreaView.FloorTitle(floor.Floor);
	}

	/// <summary>What the floor's bulk action offers: switching a floor off once every room on it is already
	/// on.</summary>
	public string FloorAction(FloorGroup<AreaConfig> floor)
	{
		ArgumentNullException.ThrowIfNull(floor);

		return AreaView.AllEnabled(floor.Items, _config.Defaults) ? "Switch off this floor" : "Switch on this floor";
	}

	/// <summary>Switches every room on a floor, as an edit: the save bar arms, nothing reaches disk.</summary>
	public void SwitchFloor(FloorGroup<AreaConfig> floor)
	{
		ArgumentNullException.ThrowIfNull(floor);

		AreaView.SwitchAll(floor.Items, !AreaView.AllEnabled(floor.Items, _config.Defaults));
		Revalidate();
	}

	// ---- the order the rooms are listed in ----

	/// <summary>The rooms this one is ordered against: the floor it is grouped under.</summary>
	private IReadOnlyList<AreaConfig> NeighboursOf(AreaConfig area) =>
		AreaGroups.FirstOrDefault(group => PositionIn(group.Items, area) >= 0)?.Items ?? [];

	// AreaConfig is a class and two rooms can hold identical values, so the row is found by identity.
	private static int PositionIn(IReadOnlyList<AreaConfig> rooms, AreaConfig area)
	{
		for (int index = 0; index < rooms.Count; index++)
		{
			if (ReferenceEquals(rooms[index], area))
				return index;
		}

		return -1;
	}

	public bool CanMoveRoomUp(AreaConfig area) => PositionIn(NeighboursOf(area), area) > 0;

	public bool CanMoveRoomDown(AreaConfig area)
	{
		IReadOnlyList<AreaConfig> neighbours = NeighboursOf(area);
		int index = PositionIn(neighbours, area);

		return index >= 0 && index < neighbours.Count - 1;
	}

	public void MoveRoomUp(AreaConfig area) => MoveRoom(area, -1);

	public void MoveRoomDown(AreaConfig area) => MoveRoom(area, 1);

	/// <summary>Swaps a room with the one above or below it on its floor.</summary>
	// Two document positions swapped, so rows between them sit on other floors and keep their own order. Nothing
	// crosses a floor boundary: the floor's own level decides where a group lands.
	private void MoveRoom(AreaConfig area, int step)
	{
		IReadOnlyList<AreaConfig> neighbours = NeighboursOf(area);
		int index = PositionIn(neighbours, area);
		int target = index + step;

		if (index < 0 || target < 0 || target >= neighbours.Count)
			return;

		int from = PositionIn(_config.Areas, area);
		int to = PositionIn(_config.Areas, neighbours[target]);

		if (from < 0 || to < 0)
			return;

		(_config.Areas[from], _config.Areas[to]) = (_config.Areas[to], _config.Areas[from]);

		Revalidate();
	}

	/// <summary>What a screen reader calls the two controls that move a room up or down its floor.</summary>
	public string MoveUpLabel(AreaConfig area) => $"Move {RoomName(area)} up";

	public string MoveDownLabel(AreaConfig area) => $"Move {RoomName(area)} down";

	/// <summary>Flips a room's power switch, writing an explicit true or false and never null.</summary>
	/// <remarks>The one edit on this page that does not wait for the save bar: only this room's slot is written,
	/// and every other edit in the draft still needs Save.</remarks>
	public void ToggleEnabled(AreaConfig area)
	{
		ArgumentNullException.ThrowIfNull(area);

		RoomWriteToken token = RoomOnDisk(area);
		bool wasClean = !_dirty;
		bool? before = area.Enabled;
		bool switchingOn = !IsEnabled(area);

		area.Enabled = switchingOn;

		// A refused write leaves the file saying the opposite of the switch, so the switch goes back with it.
		if (HasSlotOnDisk(area) && !WriteRoomNow(area, token, wasClean))
		{
			area.Enabled = before;
			Revalidate();

			return;
		}

		Revalidate();
	}

	/// <summary>This room's write token, keyed on the file rather than on the draft.</summary>
	// Off the draft, an unsaved edit to the same room — a whole floor switched, a setting changed — reads at save
	// time as somebody else having written the file, and the press is refused for a conflict nobody caused.
	private RoomWriteToken RoomOnDisk(AreaConfig area) =>
		area.AreaId is { Length: > 0 } areaId && _roomStamps.TryGetValue(areaId, out string? stamp)
			? new RoomWriteToken(areaId, stamp)
			: RoomWrite.Open(_config, area.AreaId);

	/// <summary>Records every room's slot as the document just read from disk holds it.</summary>
	private void AnchorRoomStamps(HeatingConfig onDisk)
	{
		_roomStamps.Clear();

		foreach (AreaConfig area in onDisk.Areas)
			if (area.AreaId is { Length: > 0 } areaId)
				_roomStamps[areaId] = ConfigStamp.OfArea(onDisk, areaId);
	}

	/// <summary>Whether this room has a slot on disk for a scoped write to land in.</summary>
	// RoomWrite appends where it finds no slot. A room with no area id has nothing to key on, and one added or
	// adopted since the last save is not in the file yet, so appending it would write the room a second time.
	private bool HasSlotOnDisk(AreaConfig area) =>
		LoadError is not { Length: > 0 }
		&& area.AreaId is { Length: > 0 } areaId
		&& !_unsaved.Contains(areaId);

	/// <summary>Writes one room through the same save path the whole document takes.</summary>
	/// <returns><c>true</c> once that room is on disk.</returns>
	private bool WriteRoomNow(AreaConfig area, RoomWriteToken token, bool wasClean)
	{
		// Drop any lingering confirmation, so a refused write is never shown next to a stale one.
		ClearConfirmation();

		try
		{
			RoomWriteResult write = RoomWrite.Save(_engine, token, area);

			if (!write.Result.Written)
			{
				Result = write.Result;

				return false;
			}

			Result = null;
			RestampAfterWrite(area, write.Token);

			// Only from a draft that already matched the file. With other edits pending the page is still ahead of
			// it, and saying otherwise would take the save bar away from them.
			if (wasClean)
				MarkClean();

			ShowSaveConfirmation(wasClean ? $"Saved ✓ {SavedAt()}" : "Room saved ✓ · rest not saved");

			return true;
		}
		catch (HeatingConfigException exception)
		{
			LoadError = exception.Message;

			return false;
		}
	}

	/// <summary>Re-anchors the page's tokens on the file a scoped write has just left.</summary>
	// The write is on disk before this runs, so a read that fails here costs the page a token and nothing else.
	// Reporting it as a failed write would send the switch back while the file says the opposite.
	private void RestampAfterWrite(AreaConfig area, RoomWriteToken written)
	{
		if (area.AreaId is { Length: > 0 } areaId)
			_roomStamps[areaId] = written.Stamp;

		try
		{
			// Without this the next whole-document save reports a conflict against a write this page made itself.
			_documentStamp = ConfigStamp.OfDocument(_engine.Store.Load());
		}
		catch (HeatingConfigException exception)
		{
			_logger.LogWarning(
				exception,
				"The room was written, but the document could not be read back to re-stamp the page. The next save "
				+ "of the whole document may report a conflict that a reload clears.");
		}
	}

	// ---- adding and removing a room ----

	public bool AddingRoom { get; private set; }

	public string? AddAreaId { get; private set; }

	public string? SetupNote { get; private set; }

	public void ToggleAddRoom()
	{
		if (AddingRoom)
			CloseAdd();
		else
		{
			SetupNote = null;
			CloseSetup();
			AddingRoom = true;
		}

		Notify();
	}

	private void CloseAdd()
	{
		AddingRoom = false;
		AddAreaId = null;
	}

	/// <summary>Adds a room for one Home Assistant area.</summary>
	/// <remarks>Switched off explicitly, as discovery proposes rooms. The area's name is not copied in: names
	/// resolve from the registry wherever they are shown, so writing one would freeze the room at today's name.
	/// What is written is an area id and a switch, nothing else.</remarks>
	public void AddRoom(string? areaId)
	{
		if (areaId is not { Length: > 0 })
			return;

		CloseAdd();

		_config.Areas.Add(new AreaConfig { AreaId = areaId, Enabled = false });
		_unsaved.Add(areaId);
		SetupNote = $"{AreaName(areaId)} added, off — save to open it";

		Revalidate();
	}

	/// <summary>The name Home Assistant shows for an area id, for rooms the document does not have yet.</summary>
	public string AreaName(string areaId) =>
		_areas.FirstOrDefault(area => string.Equals(area.Id, areaId, StringComparison.Ordinal))?.Name ?? areaId;

	// ---- setting rooms up again ----

	/// <summary>Null means the re-setup panel is closed; otherwise these are the rooms it opens with
	/// ticked.</summary>
	public IReadOnlyCollection<string>? SetupScope { get; private set; }

	/// <summary>Whether setting rooms up again can be offered: it rebuilds from the registry, so one that is not
	/// answering would produce an empty plan.</summary>
	public bool CanSetUpAgain => HomeAssistantIsAnswering && SetupScope is null;

	public void OpenSetupForEveryRoom()
	{
		SetupNote = null;
		CloseAdd();

		// Opens with nothing ticked, so the destructive answer is not the default one; the panel's Include all
		// is how every room is chosen.
		SetupScope = [];
		Notify();
	}

	public void CloseSetup()
	{
		SetupScope = null;
		Notify();
	}

	/// <summary>Plans a setup run for the panel; nothing changes until the plan is confirmed.</summary>
	public SetupPlan PlanSetup(IReadOnlyCollection<string> scope) => _catalog.PlanSetup(_config, scope);

	/// <summary>Carries out a confirmed setup run against the in-memory document, arming the save bar like any
	/// other edit.</summary>
	public void ApplySetup(SetupPlan plan)
	{
		ArgumentNullException.ThrowIfNull(plan);

		AreaSetupService.Apply(_config, plan);
		SetupScope = null;

		List<string> done = [];

		if (plan.Rebuilds.Count > 0)
			done.Add($"{Rooms(plan.Rebuilds.Count)} rebuilt");

		if (plan.NewAreas.Count > 0)
		{
			done.Add($"{Rooms(plan.NewAreas.Count)} added, switched off");

			foreach (AreaConfig added in plan.NewAreas)
			{
				if (added.AreaId is { Length: > 0 } areaId)
					_unsaved.Add(areaId);
			}
		}

		SetupNote = done.Count == 0
			? "Nothing changed."
			: $"{string.Join(" · ", done)} — not saved yet";

		Revalidate();

		static string Rooms(int count) => $"{count} {(count == 1 ? "room" : "rooms")}";
	}

	// ---- reload from Home Assistant ----

	/// <summary>The reload button's last result, in a few words; <c>null</c> before it is pressed.</summary>
	public string? ReloadOutcome { get; private set; }

	/// <summary>Whether the reload button may be pressed.</summary>
	/// <remarks>Reload rebuilds from the file on disk, so an edit sitting unsaved here would be silently
	/// dropped from the room count it reports.</remarks>
	public bool CanReloadFromHomeAssistant => !_dirty;

	/// <summary>Rebuilds every room from Home Assistant, through the same path a save or a restart already
	/// takes: <see cref="HeatingDocumentHost.Reload"/> and a pass over the rooms.</summary>
	public void ReloadFromHomeAssistant()
	{
		if (_dirty)
		{
			ReloadOutcome = "Save first.";
			Notify();

			return;
		}

		SaveResult result = _engine.Reload();

		if (result.Written)
			_engine.AskForAPass();

		ReloadOutcome = result.Written ? RoomsManagedLine : result.Message;
		LoadCatalog();
		Revalidate();
		Notify();
	}

	private string SavedAt() => _engine.Store.LastWrittenUtc is { } utc
		? utc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture)
		: "now";

	// ---- house modes ----

	public IReadOnlyList<string> LiveSelectOptions { get; private set; } = [];

	public IReadOnlyList<string> HouseModeOptionValues { get; private set; } = [];

	// Read separately from the house mode's: two different helpers, and comparing one document's option strings
	// against the other's list reports renames that never happened.
	public IReadOnlyList<string> LivePeriodSelectOptions { get; private set; } = [];

	public string? ActivePeriodValue { get; private set; }

	public bool PeriodSelectIsAvailable => _catalog.Knows(_config.Global.PeriodSelect?.EntityId);

	/// <summary>Whether a dropdown helper carries the time of day at all.</summary>
	public bool PeriodSelectConnected => _config.Global.PeriodSelect?.EntityId is not null;

	/// <summary>The period-select fold's header line, carrying the direction, which is what decides whether the
	/// start times in the table above mean anything.</summary>
	public string PeriodSelectSummary
	{
		get
		{
			if (_config.Global.PeriodSelect?.EntityId is not { } entity)
				return "not connected";

			int mapped = _config.Global.PeriodSelect.Options.Count(option => !option.IsEmpty);
			string rows = mapped == 1 ? "1 option mapped" : $"{mapped} options mapped";

			return Schedule.HomeAssistantDecides(_config.Global)
				? $"{entity} decides · {rows}"
				: $"{entity} follows · {rows}";
		}
	}

	/// <summary>Picks the helper that carries the time of day, and re-reads what that helper offers.</summary>
	/// <remarks>The panel writes every other <c>PeriodSelect</c> field itself; this one is here because it
	/// changes what the panel is drawn from. The mappings are not rebuilt from the new helper, where the
	/// house-mode picker does adopt: a period mapping is one string pointing at another, so adopting would
	/// invent mappings nobody chose.</remarks>
	public void SetPeriodSelectEntity(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			// Leave the object, the mappings may still be wanted; ConfigNormalizer drops it on save if empty.
			if (_config.Global.PeriodSelect is { } existing)
				existing.Entity = null;
		}
		else
		{
			// Lazily created the first time a helper is picked, so a never-adopted document acquires no block.
			(_config.Global.PeriodSelect ??= new PeriodSelectConfig()).Entity = value;
		}

		LivePeriodSelectOptions = _catalog.SelectOptionsOf(_config.Global.PeriodSelect?.EntityId);
		ActivePeriodValue = _catalog.CurrentStateOf(_config.Global.PeriodSelect?.EntityId);
		Revalidate();
	}

	/// <summary>What the period-select fold tells a picker with nothing to offer.</summary>
	public string PeriodSelectEmptyNote =>
		EmptyNote("No dropdown helpers — type an id");

	public string? ActiveModeValue { get; private set; }

	public IReadOnlyList<ModeLine> ModeLines { get; private set; } = [];

	public HouseModeOptionsDiff HelperDiff => _helperDiff;

	/// <summary>What the helper and the document disagree about, or <c>null</c> when they do not.</summary>
	public string? HelperDriftText => HouseModeSync.Drift(_helperDiff);

	/// <summary>The helper the house mode lives in, as the document names it.</summary>
	public string? HouseModeEntity => _config.Global.HouseMode?.Entity;

	/// <summary>The helper's id, or an empty string, for a sentence that reads better with the id leading.</summary>
	public string HouseModeEntityId => _config.Global.HouseMode?.EntityId ?? "";

	public bool HasHouseModeEntity => _config.Global.HouseMode?.Entity is { Length: > 0 };

	public bool HouseModeEntityIsAvailable => _catalog.Knows(_config.Global.HouseMode?.EntityId);

	/// <summary>Whether the document hands the mode to Home Assistant.</summary>
	public bool HouseModeAuthorityIsHomeAssistant =>
		_config.Global.HouseMode?.Authority == HouseModeAuthority.HomeAssistant;

	/// <summary>Whether Home Assistant owns the house mode, so the engine's own ways of setting it stand
	/// down.</summary>
	public bool HouseModeIsHomeAssistants => ModeAuthority.HomeAssistantDecides(_config.Global);

	/// <summary>What the document still configures that the authority has stood down.</summary>
	public DormantModeRules DormantRules => ModeAuthority.Dormant(_config.Global, _config.Periods);

	/// <summary>Whether the house is standing on this mode right now, as Home Assistant reports it.</summary>
	public bool IsActiveMode(ModeLine mode)
	{
		ArgumentNullException.ThrowIfNull(mode);

		return ActiveModeValue is { Length: > 0 } active && active.SameName(mode.Name);
	}

	private string HouseModeAuthorityNote => HouseModeIsHomeAssistants
		? "The engine reads this dropdown and never writes it. It still applies what each option means, but nothing "
			+ "here moves the house between them."
		: "The engine may set this dropdown itself, from a period, a switch you name, or a quiet house, and applies "
			+ "what the option means either way. Moving it by hand still works.";

	private string HouseModeUnavailableNote => HouseModeIsHomeAssistants
		? "Until it appears, the house stays in everyday heating and nothing can change the mode. Check the id for a typo."
		: "Until it appears, the engine has nowhere to write the mode, so it stays in everyday heating. Check the id for a typo.";

	/// <summary>Everything the shared panel says here, rebuilt as the direction in force changes.</summary>
	/// <remarks>The three help bodies are markup and belong to whichever design is drawing the panel, so they
	/// are left unset and filled in by the interface.</remarks>
	public SelectAuthorityCopy HouseModePanelCopy => new()
	{
		Label = "House mode",
		NoneLabel = "(none — the house has no modes)",
		EmptyNote = EmptyNote("No dropdown helpers — type an id"),
		Placeholder = "input_select.husmodus",
		AuthorityLabel = "Set by",
		AuthorityNote = HouseModeAuthorityNote,
		UnavailableNote = HouseModeUnavailableNote
	};

	private IReadOnlyList<string> ModeOptionValues() =>
	[
		.. LiveSelectOptions
			.Concat(_config.Global.HouseMode?.Options.Select(option => option.Value) ?? [])
			.Where(value => !string.IsNullOrWhiteSpace(value))
			.Select(value => value.Trim())
			.Distinct(StringComparer.OrdinalIgnoreCase)
	];

	public void SetHouseModeEntity(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			// Leave the object, its option tags may still be wanted; ConfigNormalizer drops it on save if empty.
			if (_config.Global.HouseMode is { } existing)
				existing.Entity = null;
		}
		else
		{
			// Lazily created the first time an entity is picked, so a never-adopted document acquires no block.
			(_config.Global.HouseMode ??= new HouseModeConfig()).Entity = value;
		}

		LiveSelectOptions = _catalog.SelectOptionsOf(_config.Global.HouseMode?.Entity);
		ActiveModeValue = _catalog.CurrentStateOf(_config.Global.HouseMode?.Entity);
		HouseModeOptionValues = ModeOptionValues();
		Revalidate();
	}

	/// <summary>Sets which side owns the house mode; every option keeps what it means in both directions.</summary>
	// Creating the block here is safe: ConfigNormalizer drops one carrying nothing but a non-default Authority.
	public void SetHouseModeAuthority(bool homeAssistant)
	{
		(_config.Global.HouseMode ??= new HouseModeConfig()).Authority =
			homeAssistant ? HouseModeAuthority.HomeAssistant : HouseModeAuthority.AdaptiveLighting;

		Revalidate();
	}

	public void OnHouseModeOptionsChanged() => Revalidate();

	/// <summary>Applies an edit from a mode sentence; the key carries which option it belongs to.</summary>
	/// <remarks>Decoded by <see cref="HouseSentences.TryReadModeKey"/> and never parsed here, so one file owns
	/// both halves.</remarks>
	public void OnModeEdited(SentenceEdit edit)
	{
		ArgumentNullException.ThrowIfNull(edit);

		if (!HouseSentences.TryReadModeKey(edit.Key, out int index, out string property)
			|| _config.Global.HouseMode is not { } houseMode
			|| index < 0
			|| index >= houseMode.Options.Count)
		{
			_logger.LogWarning("A mode sentence offered an edit to {Key}, which names no option on this document.", edit.Key);

			return;
		}

		HouseModeOptionConfig option = houseMode.Options[index];

		switch (property)
		{
			case nameof(HouseModeOptionConfig.ActivateAfterNoMotionMinutes):
				option.ActivateAfterNoMotionMinutes = edit.Minutes;
				break;

			case nameof(HouseModeOptionConfig.ResetPresenceGraceMinutes):
				option.ResetPresenceGraceMinutes = edit.Minutes;
				break;

			default:
				_logger.LogWarning("A mode sentence offered an edit to {Property}, which the House tab does not know how to apply.", property);

				return;
		}

		Revalidate();
	}

	/// <summary>Applies an edit from a house-wide sentence: the away debounce.</summary>
	public void OnGlobalEdited(SentenceEdit edit)
	{
		ArgumentNullException.ThrowIfNull(edit);

		switch (edit.Key)
		{
			case nameof(GlobalConfig.AwayDebounceMinutes):
				_config.Global.AwayDebounceMinutes = edit.Minutes;
				break;

			default:
				_logger.LogWarning("A house sentence offered an edit to {Key}, which the House tab does not know how to apply.", edit.Key);

				return;
		}

		Revalidate();
	}

	// ---- the House tab: labels, outdoor and sun ----

	/// <summary>Heaters more than one room commands, found as the rooms are resolved.</summary>
	public IReadOnlyList<SuspectLight> SharedLights => _engine.SharedLights;

	/// <summary>The headline over the shared-heater warning.</summary>
	public string SharedLightsHeadline => SharedLights.Count == 1
		? "1 heater in more than one room"
		: $"{SharedLights.Count} heaters in more than one room";

	public string? IncludeLabel => _config.Global.IncludeLabel;

	public string? ExcludeLabel => _config.Global.ExcludeLabel;

	public void SetIncludeLabel(string? value) => SetLabel(() => _config.Global.IncludeLabel = value);

	// Exclude is non-nullable in the model, so "none" is written as an empty string. The resolver reads empty as
	// no label: no entity can carry a nameless label, so nothing matches.
	public void SetExcludeLabel(string? value) => SetLabel(() => _config.Global.ExcludeLabel = value ?? string.Empty);

	/// <summary>A label as a person reads it: its name where the stored value is an id the house knows.</summary>
	public string LabelText(string? stored) =>
		stored is { Length: > 0 } value
			? LabelOptions.FirstOrDefault(option => string.Equals(option.Id, value, StringComparison.OrdinalIgnoreCase))?.Name ?? value
			: "none";

	private void SetLabel(Action mutate)
	{
		mutate();
		HomeAssistantIsAnswering = _catalog.IsHomeAssistantResponding(_config.Global);
		Revalidate();
	}

	public string? OutdoorSensor => _config.Global.OutdoorSensor;

	public void SetOutdoorSensor(string? value)
	{
		_config.Global.OutdoorSensor = string.IsNullOrWhiteSpace(value) ? null : value;
		Revalidate();
	}

	public string? ForecastEntity => _config.Global.ForecastEntity;

	public void SetForecastEntity(string? value)
	{
		_config.Global.ForecastEntity = string.IsNullOrWhiteSpace(value) ? null : value;
		Revalidate();
	}

	/// <summary>Which reading a warm-up is planned against, as the value its dropdown stands on.</summary>
	public string WarmUpSourceValue => _config.Global.WarmUpSource.ToString();

	public void SetWarmUpSource(string? value)
	{
		if (Enum.TryParse(value, ignoreCase: false, out WarmUpSource source) && Enum.IsDefined(source))
			_config.Global.WarmUpSource = source;

		Revalidate();
	}

	public int OutdoorSensorStaleAfterMinutes
	{
		get => _config.Global.OutdoorSensorStaleAfterMinutes;
		set
		{
			_config.Global.OutdoorSensorStaleAfterMinutes = value;
			Revalidate();
		}
	}

	/// <summary>The house's sun entity. Not nullable: unlike the outdoor sensor, every sun-anchored period needs one.</summary>
	public string SunEntity => _config.Global.SunEntity;

	public void SetSunEntity(string? value)
	{
		_config.Global.SunEntity = value ?? string.Empty;
		Revalidate();
	}

	// ---- the house: people, the master switch, the name and the fine tuning ----

	public IReadOnlyList<Sentence> AwaySentence { get; private set; } = [];

	/// <summary>The people the add-on watches, as the house card shows who is home.</summary>
	public IReadOnlyList<PersonView> People { get; private set; } = [];

	public IReadOnlyList<string> Persons => _config.Global.Persons;

	public void SetPersons(IReadOnlyList<string> persons)
	{
		_config.Global.Persons = [.. persons];
		Revalidate();
	}

	/// <summary>Addresses allowed to show these pages inside a frame.</summary>
	public IReadOnlyList<string> EmbedFrom => _config.Global.EmbedFrom;

	public void SetEmbedFrom(IReadOnlyList<string> origins)
	{
		_config.Global.EmbedFrom = [.. origins];
		Revalidate();
	}

	/// <summary>The embed list as a row's one-line value.</summary>
	public string EmbedFromValue => EmbedFrom.Count switch
	{
		0 => "nobody",
		1 => EmbedFrom[0],
		_ => $"{EmbedFrom.Count} addresses"
	};

	public string? KillSwitchEntity => _config.Global.GateEntity;

	/// <summary>Whether the add-on's own switch is in use, which is what hides the polarity dropdown.</summary>
	public bool UsesBuiltInKillSwitch => string.IsNullOrWhiteSpace(_config.Global.GateEntity);

	public string? EffectiveKillSwitchEntity => _config.Global.EffectiveGateEntity(_engine.DefaultGateEntity);

	/// <summary>Which way round the master switch reads, as the segment the select stands on.</summary>
	public string KillSwitchPolarityValue => _config.Global.GateActiveWhenOff ? "true" : "false";

	public void SetKillSwitchEntity(string? value)
	{
		_config.Global.GateEntity = value;
		Revalidate();
	}

	public void SetKillSwitchPolarity(string? value)
	{
		_config.Global.GateActiveWhenOff = string.Equals(value, "true", StringComparison.Ordinal);
		Revalidate();
	}

	/// <summary>The master switch as this page reads it live, or <c>null</c> before the built-in default resolves.</summary>
	public MasterSwitchView? MasterSwitch { get; private set; }

	/// <summary>What the master switch says on hover, matching the dashboard's own control.</summary>
	public string MasterTitle
	{
		get
		{
			if (MasterSwitch is not { } master)
				return string.Empty;

			if (!master.IsAvailable)
			{
				return master.IsReady
					? "Home Assistant doesn't know this switch."
					: "Waiting for Home Assistant.";
			}

			return master.Toggle.CanToggle
				? $"Turn adaptive heating {(master.AdaptiveLightingOn ? "off" : "on")}"
				: "This entity can't be switched on or off.";
		}
	}

	/// <summary>The line under the master switch, or <c>null</c> when the switch is simply on.</summary>
	public string? MasterNote
	{
		get
		{
			if (MasterSwitch is not { } master)
				return null;

			if (!master.IsAvailable)
				return master.IsReady ? "Switch not found" : "Waiting for Home Assistant";

			return master.AdaptiveLightingOn ? null : "Paused";
		}
	}

	/// <summary>What the (i) beside <see cref="MasterNote"/> explains.</summary>
	public string? MasterNoteMore
	{
		get
		{
			if (MasterSwitch is not { } master)
				return null;

			if (!master.IsAvailable)
			{
				return master.IsReady
					? "Home Assistant doesn't know the master switch, so its state can't be shown."
					: "Home Assistant hasn't answered yet, so the switch's state is unknown.";
			}

			return master.AdaptiveLightingOn
				? null
				: "Nothing was turned off, but no thermostat will change until it is turned back on.";
		}
	}

	/// <summary>How much <see cref="MasterNote"/> matters: a paused house needs attention.</summary>
	public InfoSeverity MasterNoteSeverity =>
		MasterSwitch is { IsAvailable: true, AdaptiveLightingOn: false } ? InfoSeverity.Bad : InfoSeverity.Neutral;

	/// <summary>Flips the master switch through the config-resolved toggle, then re-reads it.</summary>
	/// <remarks>A live Home Assistant call, not a document edit: it never arms the save bar.</remarks>
	public void ToggleMaster()
	{
		if (MasterSwitch is { IsAvailable: true, Toggle.CanToggle: true } master)
			_modes.Toggle(master.Toggle);

		MasterSwitch = _modes.GetMasterSwitch();
		Notify();
	}

	/// <summary>A label for logs and notifications, so two houses can be told apart.</summary>
	public string? HouseName
	{
		get => _config.ConfigName;
		set
		{
			_config.ConfigName = value;
			Revalidate();
		}
	}

	public int PassCadenceSeconds
	{
		get => _config.Global.PassCadenceSeconds;
		set
		{
			_config.Global.PassCadenceSeconds = value;
			Revalidate();
		}
	}

	public int SelfEchoWindowSeconds
	{
		get => _config.Global.SelfEchoWindowSeconds;
		set
		{
			_config.Global.SelfEchoWindowSeconds = value;
			Revalidate();
		}
	}

	public bool TreatAutomationsAsManual
	{
		get => _config.Global.TreatAutomationsAsManual;
		set
		{
			_config.Global.TreatAutomationsAsManual = value;
			Revalidate();
		}
	}

	// ---- the defaults every room starts with ----

	public int DefaultHoldMinutes
	{
		get => _config.Defaults.OverrideDurationMinutes;
		set
		{
			_config.Defaults.OverrideDurationMinutes = value;
			Revalidate();
		}
	}

	public bool DefaultEnabled
	{
		get => _config.Defaults.Enabled;
		set
		{
			_config.Defaults.Enabled = value;
			Revalidate();
		}
	}

	public double DefaultLowestNightTemperature
	{
		get => _config.Defaults.LowestNightTemperature;
		set
		{
			_config.Defaults.LowestNightTemperature = value;
			Revalidate();
		}
	}

	public double DefaultFrostTemperature
	{
		get => _config.Defaults.FrostTemperature;
		set
		{
			_config.Defaults.FrostTemperature = value;
			Revalidate();
		}
	}

	public bool DefaultFrostProtection
	{
		get => _config.Defaults.FrostProtection;
		set
		{
			_config.Defaults.FrostProtection = value;
			Revalidate();
		}
	}

	// ---- logging ----

	public bool CommandLog
	{
		get => _config.Global.CommandLog;
		set
		{
			_config.Global.CommandLog = value;
			Revalidate();
		}
	}

	/// <summary>The accepted word the document's log level reads as, or empty for the host's own.</summary>
	public string LogLevelValue
	{
		// Each accepted word is its LogLevel's name.
		get => LogLevelSetting.Read(_config.Global.LogLevel)?.ToString() ?? "";
		set
		{
			_config.Global.LogLevel = value is { Length: > 0 } ? value : null;
			Revalidate();
		}
	}

	public static IReadOnlyList<string> LogLevels => LogLevelSetting.Accepted;

	/// <summary>Whether the level chosen here takes effect; <c>false</c> where the host sets it.</summary>
	public bool CanSetLogLevel => _engine.CanSetLogLevel;

	public string LogLevelText =>
		!CanSetLogLevel ? "set by the host"
		: LogLevelValue is { Length: > 0 } level ? level
		: "as the host sets it";

	// ---- this installation ----

	/// <summary>Whether the document is readable, which is what decides whether anything runs.</summary>
	public bool EngineIsRunning => _engine.Fault is null;

	/// <summary>Why nothing is running, or the stand-in for never having started.</summary>
	public string EngineFault => _engine.Fault ?? "Not started yet.";

	/// <summary>How many rooms the document names to run, in words.</summary>
	public string RoomsManagedLine
	{
		get
		{
			int count = _engine.Current.ManagedAreaCount;
			return $"{count} {(count == 1 ? "room" : "rooms")} managed.";
		}
	}

	public string AreasKnownLine => $"{_areas.Count} {(_areas.Count == 1 ? "area" : "areas")} known.";

	public string ConfigFilePath => _engine.Store.FilePath;

	/// <summary>The standing warning about where the document lives, or <c>null</c> when there is none.</summary>
	public string? LocationWarning => _location?.Warning is { Length: > 0 } warning ? warning : null;

	/// <summary>What keeping the document where it is costs or buys; <c>null</c> where the host resolves no location.</summary>
	// An update, where lighting says a deploy: the add-on's image is what an update replaces.
	public string? LocationDescription => _location?.Source switch
	{
		null => null,
		ConfigLocationSource.External => "survives an update",
		ConfigLocationSource.SeededFromTree => "started fresh — survives an update",
		_ => "an update overwrites it"
	};

	public string BackupPath => _engine.Store.BackupPath;
}
