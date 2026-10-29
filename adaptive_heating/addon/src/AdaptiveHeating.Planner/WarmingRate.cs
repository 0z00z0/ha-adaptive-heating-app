namespace AdaptiveHeating.Planner;

/// <summary>One rise a room made, as degrees per hour against the outdoor temperature it was measured at.</summary>
/// <remarks>The outdoor temperature is absent where nothing was known while the room rose. Such a rise still
/// counts towards the mean and takes no part in the fitted line.</remarks>
public sealed record WarmingRateMeasurement(double? OutdoorTemperature, double DegreesPerHour, DateTimeOffset TakenAt);

public sealed record WarmingRateEstimate(double DegreesPerHour, int MeasurementCount, bool Measured);

public static class WarmingRate
{
	/// <summary>The rate to expect at an outdoor temperature. With nothing measured this is the hand-set figure.</summary>
	public static WarmingRateEstimate Estimate(
		IReadOnlyList<WarmingRateMeasurement> measurements,
		double handSetDegreesPerHour,
		double outdoorTemperature,
		DateTimeOffset asAt,
		PlannerDefaults defaults)
	{
		ArgumentNullException.ThrowIfNull(measurements);
		ArgumentNullException.ThrowIfNull(defaults);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(handSetDegreesPerHour);

		if (measurements.Count == 0)
		{
			return new WarmingRateEstimate(handSetDegreesPerHour, 0, false);
		}

		double[] weights = new double[measurements.Count];
		double halfLifeDays = defaults.MeasurementHalfLife.TotalDays;
		for (int index = 0; index < measurements.Count; index++)
		{
			double ageDays = (asAt - measurements[index].TakenAt).TotalDays;
			weights[index] = ageDays <= 0.0 ? 1.0 : Math.Pow(0.5, ageDays / halfLifeDays);
		}

		bool aLineIsPossible = measurements
			.Where(measurement => measurement.OutdoorTemperature.HasValue)
			.Select(measurement => measurement.OutdoorTemperature!.Value)
			.Distinct()
			.Count() >= 2;

		double estimate = aLineIsPossible
			? OnALine(measurements, weights, outdoorTemperature)
			: WeightedMean(measurements, weights);

		// Extrapolating the line past the weather it was fitted in must never promise a rate the room
		// has never managed, because a rate too high starts a warm-up too late.
		double fastestSeen = measurements.Max(measurement => measurement.DegreesPerHour);
		estimate = Math.Min(estimate, fastestSeen);
		if (estimate <= 0.0)
		{
			estimate = Math.Min(handSetDegreesPerHour, fastestSeen);
		}

		return new WarmingRateEstimate(estimate, measurements.Count, true);
	}

	private static double WeightedMean(IReadOnlyList<WarmingRateMeasurement> measurements, double[] weights)
	{
		double total = 0.0;
		double weighted = 0.0;
		for (int index = 0; index < measurements.Count; index++)
		{
			total += weights[index];
			weighted += weights[index] * measurements[index].DegreesPerHour;
		}

		return total <= 0.0 ? measurements.Average(measurement => measurement.DegreesPerHour) : weighted / total;
	}

	private static double OnALine(IReadOnlyList<WarmingRateMeasurement> measurements, double[] weights, double outdoorTemperature)
	{
		double sumW = 0.0;
		double sumWx = 0.0;
		double sumWy = 0.0;
		double sumWxx = 0.0;
		double sumWxy = 0.0;

		for (int index = 0; index < measurements.Count; index++)
		{
			// A rise with no outdoor figure beside it cannot sit on the line, so it is left to the mean.
			if (measurements[index].OutdoorTemperature is not { } known)
				continue;

			double w = weights[index];
			double x = known;
			double y = measurements[index].DegreesPerHour;
			sumW += w;
			sumWx += w * x;
			sumWy += w * y;
			sumWxx += w * x * x;
			sumWxy += w * x * y;
		}

		double denominator = (sumW * sumWxx) - (sumWx * sumWx);
		if (Math.Abs(denominator) < 1e-9)
		{
			return WeightedMean(measurements, weights);
		}

		double slope = ((sumW * sumWxy) - (sumWx * sumWy)) / denominator;
		double intercept = (sumWy - (slope * sumWx)) / sumW;
		return intercept + (slope * outdoorTemperature);
	}
}
