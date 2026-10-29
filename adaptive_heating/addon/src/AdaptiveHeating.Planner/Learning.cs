namespace AdaptiveHeating.Planner;

/// <summary>What one room was doing at one moment, as the add-on reads it off that room's thermostat.</summary>
/// <remarks>The power reading is what the room's meter said, and is absent where the meter is quiet. A room with
/// no meter fitted at all is ordinary rather than a fault, and says so separately.</remarks>
public sealed record RoomObservation(
	DateTimeOffset At,
	double? RoomTemperature,
	double Target,
	bool HeaterCommandedOn,
	bool WarmUpRunning,
	double? OutdoorTemperature,
	double? PowerWatts,
	bool HasAPowerMeter);

/// <summary>What became of a rise.</summary>
public enum RiseVerdict
{
	/// <summary>Nothing was being watched.</summary>
	Nothing,

	StillRising,

	Recorded,

	/// <summary>The heater was commanded on and its meter never moved, so the rise teaches nothing.</summary>
	NoPowerDrawn,

	/// <summary>Nothing closed the relay while the room rose, so the rise is the weather's and not the heater's.</summary>
	HeaterNeverRan,

	TooSmallToCount,

	/// <summary>The target moved down or the reading went, so there is no rise to a higher temperature any more.</summary>
	Abandoned,
}

public sealed record RiseOutcome(RiseVerdict Verdict, WarmingRateMeasurement? Measurement)
{
	public static readonly RiseOutcome Nothing = new(RiseVerdict.Nothing, null);
}

/// <summary>
/// One room's rises, turned into degrees an hour. A point is taken from every rise towards a higher temperature,
/// whether a warm-up planned it or a boundary simply raised the temperature.
/// </summary>
/// <remarks>A paced rise spends part of its time with the relay open, so the rate measured is at or below the rate
/// the room can manage. That is the safe end: a rate read low starts the next warm-up early.</remarks>
public sealed class WarmingRateWatcher
{
	private readonly PlannerDefaults _defaults;

	private DateTimeOffset _startedAt;
	private double _startedFrom;
	private double _target;
	private double _outdoorSum;
	private int _outdoorSeen;
	private bool _running;
	private bool _heaterRan;
	private bool _powerWasDrawn;

	public WarmingRateWatcher(PlannerDefaults defaults) =>
		_defaults = defaults ?? throw new ArgumentNullException(nameof(defaults));

	public RiseOutcome Observe(RoomObservation observation)
	{
		ArgumentNullException.ThrowIfNull(observation);

		if (observation.RoomTemperature is not { } room)
			return Stop(RiseVerdict.Abandoned);

		if (!_running)
		{
			if (room <= observation.Target - _defaults.SmallestRiseWorthMeasuring)
				Begin(observation, room);

			return RiseOutcome.Nothing;
		}

		// A target lowered mid-rise ends the rise: what follows is no longer a rise towards a higher temperature.
		if (observation.Target < _target - 0.01)
			return Stop(RiseVerdict.Abandoned);

		_target = observation.Target;
		Note(observation);

		return room >= _target ? Finish(observation, room) : new RiseOutcome(RiseVerdict.StillRising, null);
	}

	private void Begin(RoomObservation observation, double room)
	{
		_running = true;
		_startedAt = observation.At;
		_startedFrom = room;
		_target = observation.Target;
		_outdoorSum = 0;
		_outdoorSeen = 0;
		_heaterRan = false;
		_powerWasDrawn = false;
		Note(observation);
	}

	private void Note(RoomObservation observation)
	{
		if (observation.OutdoorTemperature is { } outdoor)
		{
			_outdoorSum += outdoor;
			_outdoorSeen++;
		}

		if (observation.HeaterCommandedOn)
			_heaterRan = true;

		if (observation.PowerWatts >= _defaults.PowerThatCountsAsRunning)
			_powerWasDrawn = true;
	}

	private RiseOutcome Finish(RoomObservation observation, double room)
	{
		TimeSpan took = observation.At - _startedAt;
		double rise = room - _startedFrom;
		double? outdoor = _outdoorSeen == 0 ? null : _outdoorSum / _outdoorSeen;

		if (!_heaterRan)
			return Stop(RiseVerdict.HeaterNeverRan);

		// A room that fails to warm because its own switch is off would otherwise teach itself a false rate, and
		// that number would stay after somebody switched the heater back on.
		if (observation.HasAPowerMeter && !_powerWasDrawn)
			return Stop(RiseVerdict.NoPowerDrawn);

		if (rise < _defaults.SmallestRiseWorthMeasuring || took < _defaults.ShortestRiseWorthMeasuring)
			return Stop(RiseVerdict.TooSmallToCount);

		WarmingRateMeasurement measurement = new(outdoor, rise / took.TotalHours, observation.At);
		Stop(RiseVerdict.Nothing);

		return new RiseOutcome(RiseVerdict.Recorded, measurement);
	}

	private RiseOutcome Stop(RiseVerdict verdict)
	{
		bool wasRunning = _running;
		_running = false;

		return wasRunning ? new RiseOutcome(verdict, null) : RiseOutcome.Nothing;
	}
}

/// <summary>How far below its target a room settled, and the outdoor holding term that would have closed the gap.</summary>
public sealed record HoldingFit(double PerDegree, double MeanShortfall, double OutdoorTemperature, DateTimeOffset TakenAt);

/// <summary>
/// One room's settled stretches, turned into the outdoor part of the holding duty. Nobody sets that number: it is
/// how far below its target the room settles at each outdoor temperature that moves it.
/// </summary>
public sealed class HoldingTermWatcher
{
	private readonly PlannerDefaults _defaults;

	private DateTimeOffset _settledAt;
	private double? _target;
	private double _shortfallSum;
	private double _outdoorSum;
	private int _samples;

	public HoldingTermWatcher(PlannerDefaults defaults) =>
		_defaults = defaults ?? throw new ArgumentNullException(nameof(defaults));

	/// <summary>The corrected term, once the room has held still long enough for the shortfall to mean something.</summary>
	public HoldingFit? Observe(RoomObservation observation, double termInForce)
	{
		ArgumentNullException.ThrowIfNull(observation);

		bool settled = observation.RoomTemperature is { } room
			&& !observation.WarmUpRunning
			&& observation.OutdoorTemperature.HasValue
			&& Math.Abs(observation.Target - room) < _defaults.FullOutputBelow;

		// A stretch is one room holding one temperature. A target change starts a new one, because the shortfall
		// at the old temperature says nothing about the new.
		if (!settled || _target is not { } held || Math.Abs(observation.Target - held) > 0.01)
		{
			Restart(observation, settled);
			return null;
		}

		_shortfallSum += observation.Target - observation.RoomTemperature!.Value;
		_outdoorSum += observation.OutdoorTemperature!.Value;
		_samples++;

		if (observation.At - _settledAt < _defaults.SettledStretchBeforeAMeasurement)
			return null;

		double shortfall = _shortfallSum / _samples;
		double outdoor = _outdoorSum / _samples;
		double above = Math.Max(1.0, observation.Target - outdoor);

		// The duty that would have closed the shortfall, spread over the degrees the room sits above the weather.
		// A room short of its target takes the margin above what the measurements fit; one that overshot takes the
		// correction plain, because a margin there would err away from the end that keeps the room warm.
		double correction = shortfall / (_defaults.FullOutputBelow * above);
		if (correction > 0)
			correction = LearntNumbers.FromTheHoldingFit(correction, _defaults);

		Restart(observation, settled: true);

		return new HoldingFit(Math.Max(0.0, termInForce + correction), shortfall, outdoor, observation.At);
	}

	private void Restart(RoomObservation observation, bool settled)
	{
		_settledAt = observation.At;
		_target = settled ? observation.Target : null;
		_shortfallSum = 0;
		_outdoorSum = 0;
		_samples = 0;
	}
}
