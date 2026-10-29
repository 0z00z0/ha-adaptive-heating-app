namespace AdaptiveHeating.Planner;

public enum SensorExclusion
{
	None,
	Quiet,
	Stuck,
	FarFromTheOthers,
	BothOfADisagreeingPair,
}

public sealed record SensorReading(string SensorId, double Temperature, DateTimeOffset ReportedAt, DateTimeOffset LastMovedAt);

public sealed record SensorVerdict(string SensorId, double Temperature, SensorExclusion Excluded);

/// <summary>What a room's sensors add up to, and which of them were left out and why.</summary>
public sealed record RoomReading(double? Temperature, int RestingOn, int SensorsInTheRoom, IReadOnlyList<SensorVerdict> Verdicts)
{
	public bool HasATemperature => Temperature.HasValue;

	public IEnumerable<SensorVerdict> LeftOut => Verdicts.Where(verdict => verdict.Excluded != SensorExclusion.None);
}

public static class RoomSensors
{
	/// <summary>The room's temperature from the sensors that have reported recently, with a verdict on every sensor.</summary>
	public static RoomReading Read(IReadOnlyList<SensorReading> sensors, DateTimeOffset asAt, PlannerDefaults defaults)
	{
		ArgumentNullException.ThrowIfNull(sensors);
		ArgumentNullException.ThrowIfNull(defaults);

		Dictionary<string, SensorExclusion> excluded = new(StringComparer.Ordinal);
		List<SensorReading> live = [];

		foreach (SensorReading sensor in sensors)
		{
			if (asAt - sensor.ReportedAt > defaults.SensorFreshness)
			{
				excluded[sensor.SensorId] = SensorExclusion.Quiet;
			}
			else if (asAt - sensor.LastMovedAt >= defaults.StuckReading)
			{
				// A live sensor in a real room always wanders a little. A flat reading is a dying battery.
				excluded[sensor.SensorId] = SensorExclusion.Stuck;
			}
			else
			{
				live.Add(sensor);
			}
		}

		if (live.Count == 2 && Math.Abs(live[0].Temperature - live[1].Temperature) > defaults.SensorSpread)
		{
			// Two sensors cannot say which of them is lying, so the room loses its temperature with both still reporting.
			excluded[live[0].SensorId] = SensorExclusion.BothOfADisagreeingPair;
			excluded[live[1].SensorId] = SensorExclusion.BothOfADisagreeingPair;
			live.Clear();
		}
		else if (live.Count >= 3)
		{
			double middle = Median(live.Select(sensor => sensor.Temperature));
			foreach (SensorReading sensor in live.ToList())
			{
				if (Math.Abs(sensor.Temperature - middle) > defaults.SensorSpread)
				{
					excluded[sensor.SensorId] = SensorExclusion.FarFromTheOthers;
					live.Remove(sensor);
				}
			}
		}

		List<SensorVerdict> verdicts = [.. sensors.Select(sensor =>
			new SensorVerdict(sensor.SensorId, sensor.Temperature, excluded.GetValueOrDefault(sensor.SensorId, SensorExclusion.None)))];

		double? temperature = live.Count == 0 ? null : live.Average(sensor => sensor.Temperature);
		return new RoomReading(temperature, live.Count, sensors.Count, verdicts);
	}

	private static double Median(IEnumerable<double> values)
	{
		double[] sorted = [.. values.Order()];
		int middle = sorted.Length / 2;
		return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2.0;
	}
}
