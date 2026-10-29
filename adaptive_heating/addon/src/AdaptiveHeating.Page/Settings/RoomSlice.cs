using System.Text.Json.Serialization;

using AdaptiveHeating.Planner;

using ChangeOrigin = AdaptiveHeating.Planner.ChangeOrigin;

namespace AdaptiveHeating.Page.Settings;

/// <summary>What a room's sensors are held to, every value settable per room.</summary>
/// <param name="FreshWindow">How recently a sensor must have reported to count.</param>
/// <param name="StuckWindow">How long a reading may sit at one value before it reads as stuck.</param>
/// <param name="Spread">How far a fresh reading may sit from the others in the same room.</param>
/// <param name="WindowDelay">The delay each way before an open window stops the room and a closed one resumes it.</param>
/// <param name="BorrowsFrom">The area of the room this one takes its reading from, or <c>null</c> where it has sensors of its own.</param>
public sealed record RoomSensorSettings(
	TimeSpan FreshWindow,
	TimeSpan StuckWindow,
	double Spread,
	TimeSpan WindowDelay,
	string? BorrowsFrom)
{
	public static RoomSensorSettings Standard(PlannerDefaults defaults)
	{
		ArgumentNullException.ThrowIfNull(defaults);

		return new(defaults.SensorFreshness, defaults.StuckReading, defaults.SensorSpread, TimeSpan.FromMinutes(2), null);
	}
}

/// <summary>The three numbers the system works out for a room, each with its own reset and its own lock.</summary>
public sealed record RoomLearntNumbers(LearntNumber WarmingRate, LearntNumber OutdoorTerm, LearntNumber Band)
{
	public static RoomLearntNumbers Starting(PlannerDefaults defaults)
	{
		ArgumentNullException.ThrowIfNull(defaults);

		return new(
			LearntNumber.Starting(0.5, SafeDirection.Lower),
			LearntNumber.Starting(defaults.HoldingTermStartingPerDegree, SafeDirection.Higher),
			LearntNumber.Starting(defaults.BandStartingValue, SafeDirection.Lower, ceiling: defaults.BandCeiling));
	}
}

/// <summary>Which of a room's three learnt numbers a reset or a lock is about.</summary>
public enum LearntKind
{
	WarmingRate,
	OutdoorTerm,
	Band
}

/// <summary>When one mode's value was set and from where. The value itself is in the settings document.</summary>
[method: JsonConstructor]
public readonly record struct ModeStamp(Stamp Stamp, ChangeOrigin Origin)
{
	/// <summary>The planner's own contest, so a stamp here wins and loses exactly as a stamped number does.</summary>
	public bool IsReplacedBy(ModeStamp arriving) =>
		new Stamped<bool>(false, Stamp, Origin).IsReplacedBy(new Stamped<bool>(false, arriving.Stamp, arriving.Origin));
}

/// <summary>One room, as the page edits it. Saving sends this slice and never the rest of the document.</summary>
/// <param name="Id">The room id the integration minted, which both halves name the room by.</param>
/// <param name="Name">What a person calls it.</param>
/// <param name="Sensors">The per-room sensor values.</param>
/// <param name="Learnt">The three learnt numbers.</param>
public sealed record RoomSlice(
	string Id,
	string Name,
	RoomSensorSettings Sensors,
	RoomLearntNumbers Learnt)
{
	/// <summary>The area the room is in now, which joins it to its entry in the settings document.</summary>
	public string? AreaId { get; init; }

	/// <summary>The entity id of this room's thermostat, which is what every action targets.</summary>
	/// <remarks>Filled from what the integration reports. Nothing is called for a room without one.</remarks>
	public string? Thermostat { get; init; }

	/// <summary>
	/// Whether the room has no temperature reading at all: no sensor of its own, and no neighbour to borrow from.
	/// Such a room takes on or off from a mode where another room takes a temperature.
	/// </summary>
	// Reported by the integration, because it follows from how the room was set up there. A room whose sensors have
	// merely gone quiet is not this case and keeps its temperatures.
	public bool NoReadingAtAll { get; init; }

	/// <summary>One on-or-off cell per mode, which is what a room with no reading carries in place of a temperature.</summary>
	public IReadOnlyDictionary<HeatingMode, Stamped<bool>> ModeSwitches { get; init; } =
		new Dictionary<HeatingMode, Stamped<bool>>();

	/// <summary>One temperature per mode: the value from the settings document, the stamp from <see cref="ModeStamps"/>.</summary>
	// Filled on every read and never written to the note, so a value has one home.
	[JsonIgnore]
	public IReadOnlyDictionary<HeatingMode, Stamped<double>> ModeTemperatures { get; init; } =
		new Dictionary<HeatingMode, Stamped<double>>();

	/// <summary>When each mode's value was last set, keyed by mode; the values are in the settings document.</summary>
	public IReadOnlyDictionary<HeatingMode, ModeStamp> ModeStamps { get; init; } =
		new Dictionary<HeatingMode, ModeStamp>();

	/// <summary>The temperature the add-on last asked of this room, kept so a restart does not read it as a hand.</summary>
	public double? Commanded { get; init; }

	/// <summary>When the add-on last sent that command, which a thermostat's rounded echo is measured from.</summary>
	public DateTimeOffset? CommandedAt { get; init; }

	/// <summary>Every rise this room has made, which is what its warming rate is estimated from.</summary>
	public IReadOnlyList<WarmingRateMeasurement> RateMeasurements { get; init; } = [];

	/// <summary>Every settled stretch read against the meter, which the room's heat loss is estimated from.</summary>
	public IReadOnlyList<HeatLossMeasurement> HeatLossMeasurements { get; init; } = [];

	/// <summary>What the next period gives this room, which a night setback warms it back to.</summary>
	[JsonIgnore]
	public double? NextPeriodTemperature { get; init; }

	public double TemperatureFor(HeatingMode mode) =>
		ModeTemperatures.TryGetValue(mode, out Stamped<double> held) ? held.Value : 0;

	/// <summary>Whether the period in force takes this room's own temperature rather than the house value.</summary>
	[JsonIgnore]
	public bool? LevelsFromRoom { get; init; }

	/// <summary>The battery entities on the room's temperature sensors' own devices, as the last pass found them.</summary>
	[JsonIgnore]
	public IReadOnlyList<AdaptiveLighting.Engine.MotionBattery>? Batteries { get; init; }

	[JsonIgnore]
	public bool Borrows => Sensors.BorrowsFrom is { Length: > 0 };
}
