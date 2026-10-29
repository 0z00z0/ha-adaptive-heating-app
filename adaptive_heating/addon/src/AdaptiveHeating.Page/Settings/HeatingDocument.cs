using System.Text.Json.Serialization;

using AdaptiveHeating.Planner;

using ChangeOrigin = AdaptiveHeating.Planner.ChangeOrigin;

namespace AdaptiveHeating.Page.Settings;

/// <summary>Everything the settings page reads, as one snapshot.</summary>
/// <param name="Rooms">Every room the house's Areas hold, keyed by the area id.</param>
/// <param name="Versions">What each half runs.</param>
public sealed record HeatingDocument(
	IReadOnlyList<RoomSlice> Rooms,
	HalfVersions Versions)
{
	/// <summary>The calendar the planned arrivals are read from, by entity id, or <c>null</c> where none is chosen.</summary>
	/// <remarks>Any calendar will do: reading one is the same whoever keeps it.</remarks>
	public string? ArrivalCalendar { get; init; }

	/// <summary>The calendar each finished warm-up is written to, by entity id, or <c>null</c> where none is chosen.</summary>
	/// <remarks>A local one and no other, so the record stays on the box it is about.</remarks>
	public string? RecordCalendar { get; init; }

	/// <summary>Every calendar Home Assistant reports now, which is what the two settings are chosen from.</summary>
	// A reading and not a setting: the house is read again within a minute of a restart, and a stored list would
	// go stale against Home Assistant.
	[JsonIgnore]
	public IReadOnlyList<CalendarOnOffer> CalendarsOffered { get; init; } = [];

	/// <summary>The name of the period in force, or <c>null</c> where none is.</summary>
	[JsonIgnore]
	public string? PeriodInForce { get; init; }

	/// <summary>The key of the period in force, which a period entering is told by, or <c>null</c> where none is.</summary>
	[JsonIgnore]
	public string? PeriodKeyInForce { get; init; }

	/// <summary>When the next period starts, or <c>null</c> where none can be placed or the period dropdown decides.</summary>
	[JsonIgnore]
	public DateTimeOffset? NextPeriodStartsAt { get; init; }

	/// <summary>Whether the house-mode option in force is a sleep option marked for night setback.</summary>
	[JsonIgnore]
	public bool NightSetbackInForce { get; init; }

	/// <summary>What each room is currently being told to hold, and the mode in force over the whole cabin.</summary>
	// Worked out afresh on every pass and mirroring temperatures the rooms already carry, so it is never written
	// to the settings file.
	[JsonIgnore]
	public InForceRecord InForce { get; init; } = InForceRecord.Empty;

	/// <summary>What each room's thermostat reads and holds now, by the id both halves name the room by.</summary>
	// A reading, not a setting: a restart reads it again within a minute.
	[JsonIgnore]
	public IReadOnlyDictionary<string, RoomNow> Now { get; init; } =
		new Dictionary<string, RoomNow>(StringComparer.Ordinal);

	/// <summary>What one room reads and holds now, or <c>null</c> where nothing has been heard about it.</summary>
	public RoomNow? NowFor(string roomId) => Now.GetValueOrDefault(roomId);

	/// <summary>The room in one area now, or <c>null</c> where the integration has named none there yet.</summary>
	public RoomSlice? RoomIn(string? areaId) =>
		areaId is { Length: > 0 } ? Rooms.FirstOrDefault(room => string.Equals(room.AreaId, areaId, StringComparison.Ordinal)) : null;

	/// <summary>What the last pass decided about each room's own gates, by room id.</summary>
	[JsonIgnore]
	public IReadOnlyDictionary<string, RoomVerdict> Verdicts { get; init; } =
		new Dictionary<string, RoomVerdict>(StringComparer.Ordinal);

	public RoomVerdict? VerdictFor(string roomId) => Verdicts.GetValueOrDefault(roomId);

	/// <summary>The temperature one room is being told to hold, or <c>null</c> where none is in force.</summary>
	public Stamped<double>? ToldFor(string roomId) =>
		InForce.Temperatures.TryGetValue(roomId, out Stamped<double> told) ? told : null;

	/// <summary>The rooms holding a temperature somebody set at the thermostat.</summary>
	[JsonIgnore]
	public IReadOnlyList<RoomSlice> SetByHand =>
		[.. Rooms.Where(room => ToldFor(room.Id) is { Origin: ChangeOrigin.Thermostat })];

	/// <summary>
	/// Rewrites every stamp written against an unset clock to the moment the clock was set, so a change made
	/// during an outage stands newer than anything before it and older than anything after.
	/// </summary>
	public HeatingDocument WhenTheClockIsSet(DateTimeOffset networkTime) =>
		this with
		{
			Rooms = [.. Rooms.Select(room => room with { ModeStamps = Rewrite(room.ModeStamps, networkTime) })]
		};

	/// <summary>What the add-on starts on before it has heard from the integration.</summary>
	/// <remarks>A room is an Area, so an add-on that has not yet read the registries knows of none,
	/// and the page says so rather than showing an empty table as though it were a finished one.</remarks>
	public static HeatingDocument NothingHeardYet() =>
		new([],
			new HalfVersions(HalfVersions.AddOnVersion, null, Planner.Vocabulary.Version));

	private static Dictionary<HeatingMode, ModeStamp> Rewrite(
		IReadOnlyDictionary<HeatingMode, ModeStamp> held,
		DateTimeOffset networkTime)
	{
		Dictionary<HeatingMode, ModeStamp> rewritten = [];

		foreach (KeyValuePair<HeatingMode, ModeStamp> one in held)
			rewritten[one.Key] = one.Value with { Stamp = one.Value.Stamp.WhenTheClockIsSet(networkTime) };

		return rewritten;
	}
}

/// <summary>What a save was allowed to do.</summary>
/// <param name="Written">Whether the slice reached the store.</param>
/// <param name="Message">Why not, where it did not. Empty otherwise.</param>
public readonly record struct SaveOutcome(bool Written, string Message)
{
	public static SaveOutcome Ok { get; } = new(true, "");

	public static SaveOutcome Refused(string message) => new(false, message);
}

/// <summary>Where the page reads and writes. One room's slice at a time, never the whole document.</summary>
/// <remarks>Nothing on the page knows which store it is talking to: one holds the settings for as long as the
/// process runs, and the add-on wraps that one in a file and in the calls to the thermostats.</remarks>
public interface ISettingsStore
{
	HeatingDocument Read();

	/// <summary>Raised when something other than this page changed a number, so the table states what is held.</summary>
	event Action? Changed;

	/// <summary>Takes a document from somewhere other than the page: a restart, the integration, or the clock being set.</summary>
	void Receive(HeatingDocument document);

	/// <summary>
	/// Takes what the rooms are doing: what each thermostat reads and holds, and what each is being told. Not a
	/// setting, so nothing is written to a file and nothing is sent to a room.
	/// </summary>
	void ReceiveWhatTheRoomsAreDoing(
		InForceRecord inForce,
		IReadOnlyDictionary<string, RoomNow> now,
		IReadOnlyDictionary<string, RoomVerdict>? verdicts = null);

	/// <summary>
	/// Takes the calendars Home Assistant reports now, so the page can offer them. Not a setting, so nothing is
	/// written to a file.
	/// </summary>
	void ReceiveTheCalendarsOffered(IReadOnlyList<CalendarOnOffer> calendars);

	/// <summary>Writes which calendar the arrivals are read from and which one the record is written to.</summary>
	/// <remarks>Both at once, because they sit on one screen behind one save. An empty value clears a setting.</remarks>
	SaveOutcome SaveCalendars(string? arrivals, string? record);

	/// <summary>Stamps the modes whose value a save moved in the settings document; an older stamp is refused.</summary>
	SaveOutcome SaveModeStamps(string roomId, IReadOnlyCollection<HeatingMode> modes, Stamp stamp);

	SaveOutcome SaveSensorSettings(string roomId, RoomSensorSettings settings);

	/// <summary>On or off in one mode, for a room with no reading; an older stamp is refused.</summary>
	SaveOutcome SaveModeSwitch(string roomId, HeatingMode mode, bool on, Stamp stamp);

	/// <summary>Returns a learnt number to its safe starting value and drops the measurements behind it.</summary>
	SaveOutcome ResetLearnt(string roomId, LearntKind kind);

	/// <summary>A set value locks the number; a <c>null</c> value releases the lock and hands back the system's own.</summary>
	SaveOutcome SetLearntByHand(string roomId, LearntKind kind, double? value);

	/// <summary>The integration moves the room's thermostat to area <paramref name="toId"/>; a refusal changes nothing.</summary>
	Task<SaveOutcome> CarryRoomAsync(string fromId, string toId, CancellationToken token = default);

	SaveOutcome LinkRoom(string fromId, string toId);
}
