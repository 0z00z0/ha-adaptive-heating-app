namespace AdaptiveHeating.Planner;

/// <summary>Every number the planner chooses rather than derives, in one place.</summary>
public sealed record PlannerDefaults
{
	public static PlannerDefaults Standard { get; } = new();

	/// <summary>The fraction of the measured warming rate a warm-up is planned on.</summary>
	public double WarmUpPlanningFraction { get; init; } = 0.8;

	/// <summary>What the fitted outdoor holding term is multiplied by before it is used.</summary>
	public double HoldingTermMargin { get; init; } = 1.1;

	public double HoldingTermStartingPerDegree { get; init; } = 0.005;

	public double BandStartingValue { get; init; } = 0.5;

	public double BandCeiling { get; init; } = 1.0;

	/// <summary>How many measurements in a row must agree before a number moves away from its safe end.</summary>
	public int AgreementsBeforeRelaxing { get; init; } = 5;

	/// <summary>How far a fresh reading may sit from the others in its room before it is left out.</summary>
	public double SensorSpread { get; init; } = 2.0;

	public TimeSpan SensorFreshness { get; init; } = TimeSpan.FromHours(1);

	/// <summary>How long a reading may sit at exactly one value before it reads as stuck.</summary>
	public TimeSpan StuckReading { get; init; } = TimeSpan.FromHours(6);

	/// <summary>How long a measurement takes to count for half of what a fresh one counts for.</summary>
	public TimeSpan MeasurementHalfLife { get; init; } = TimeSpan.FromDays(30);

	/// <summary>How far below its target a room runs at full output. Mirrors the figure the integration's loop holds.</summary>
	// Only used to turn a settled room's shortfall into the duty that would have closed it.
	public double FullOutputBelow { get; init; } = 1.5;

	/// <summary>The smallest rise worth taking a warming rate from.</summary>
	public double SmallestRiseWorthMeasuring { get; init; } = 0.5;

	/// <summary>The shortest rise worth taking a warming rate from.</summary>
	public TimeSpan ShortestRiseWorthMeasuring { get; init; } = TimeSpan.FromMinutes(15);

	/// <summary>The draw at which a heater counts as having run.</summary>
	public double PowerThatCountsAsRunning { get; init; } = 10.0;

	/// <summary>How long a room must sit settled before its shortfall is worth one measurement.</summary>
	public TimeSpan SettledStretchBeforeAMeasurement { get; init; } = TimeSpan.FromMinutes(30);

	public TimeSpan SettledStretchBeforeAHeatLoss { get; init; } = TimeSpan.FromHours(1);

	public double SmallestGapForAHeatLoss { get; init; } = 3.0;

	public double SteadyWithin { get; init; } = 0.2;

	public double SmallestSetback { get; init; } = 0.5;

	public double SetbackStep { get; init; } = 0.5;
}
