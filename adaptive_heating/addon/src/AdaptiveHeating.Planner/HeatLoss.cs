namespace AdaptiveHeating.Planner;

/// <summary>One settled stretch: the watts a room lost per degree above the outdoor temperature, and its heater's full draw.</summary>
public sealed record HeatLossMeasurement(double WattsPerDegree, double HeaterWatts, DateTimeOffset TakenAt);

public sealed record HeatLossEstimate(double WattsPerDegree, double HeaterWatts);

public static class HeatLoss
{
	/// <summary>The recency-weighted mean of the measurements, or <c>null</c> where none is taken.</summary>
	public static HeatLossEstimate? Estimate(
		IReadOnlyList<HeatLossMeasurement> measurements,
		DateTimeOffset asAt,
		PlannerDefaults defaults)
	{
		ArgumentNullException.ThrowIfNull(measurements);
		ArgumentNullException.ThrowIfNull(defaults);

		if (measurements.Count == 0)
			return null;

		double total = 0.0;
		double loss = 0.0;
		double heater = 0.0;

		foreach (HeatLossMeasurement measurement in measurements)
		{
			double ageDays = (asAt - measurement.TakenAt).TotalDays;
			double weight = ageDays <= 0.0 ? 1.0 : Math.Pow(0.5, ageDays / defaults.MeasurementHalfLife.TotalDays);
			total += weight;
			loss += weight * measurement.WattsPerDegree;
			heater += weight * measurement.HeaterWatts;
		}

		return total <= 0.0 ? null : new HeatLossEstimate(loss / total, heater / total);
	}
}

/// <summary>One room's settled stretches read against its meter: a room holding its temperature draws what it loses.</summary>
public sealed class HeatLossWatcher
{
	private readonly PlannerDefaults _defaults;

	private DateTimeOffset _settledAt;
	private double? _target;
	private double _startedFrom;
	private RoomObservation? _last;
	private double _wattHours;
	private double _degreeHours;
	private double _hours;
	private double _heaterWatts;

	public HeatLossWatcher(PlannerDefaults defaults) =>
		_defaults = defaults ?? throw new ArgumentNullException(nameof(defaults));

	public HeatLossMeasurement? Observe(RoomObservation observation)
	{
		ArgumentNullException.ThrowIfNull(observation);

		bool settled = observation.RoomTemperature is { } room
			&& !observation.WarmUpRunning
			&& observation.OutdoorTemperature.HasValue
			&& observation.HasAPowerMeter
			&& observation.PowerWatts.HasValue
			&& Math.Abs(observation.Target - room) < _defaults.FullOutputBelow;

		if (!settled || _target is not { } held || Math.Abs(observation.Target - held) > 0.01 || _last is not { } last)
		{
			Restart(observation, settled);
			return null;
		}

		// Time-weighted: a pass lands on every relay change, so a plain mean would count the switching moments twice.
		double hours = (observation.At - last.At).TotalHours;
		_wattHours += last.PowerWatts!.Value * hours;
		_degreeHours += (last.RoomTemperature!.Value - last.OutdoorTemperature!.Value) * hours;
		_hours += hours;
		Note(observation);

		if (observation.At - _settledAt < _defaults.SettledStretchBeforeAHeatLoss)
			return null;

		double gap = _degreeHours / _hours;
		double watts = _wattHours / _hours;
		double heaterWatts = _heaterWatts;
		bool steady = Math.Abs(observation.RoomTemperature!.Value - _startedFrom) <= _defaults.SteadyWithin;
		Restart(observation, settled: true);

		// A room still cooling or warming draws less or more than it loses.
		if (!steady || gap < _defaults.SmallestGapForAHeatLoss || heaterWatts < _defaults.PowerThatCountsAsRunning || watts <= 0.0)
			return null;

		return new HeatLossMeasurement(watts / gap, heaterWatts, observation.At);
	}

	private void Note(RoomObservation observation)
	{
		_last = observation;

		if (observation.HeaterCommandedOn && observation.PowerWatts is { } drawn)
			_heaterWatts = Math.Max(_heaterWatts, drawn);
	}

	private void Restart(RoomObservation observation, bool settled)
	{
		_settledAt = observation.At;
		_startedFrom = observation.RoomTemperature ?? 0.0;
		_target = settled ? observation.Target : null;
		_last = null;
		_wattHours = 0;
		_degreeHours = 0;
		_hours = 0;
		_heaterWatts = 0;

		if (settled)
			Note(observation);
	}
}
