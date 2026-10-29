namespace AdaptiveHeating.Planner;

/// <summary>Which way a learnt number errs. The safe end is the one that costs electricity rather than a cold room.</summary>
public enum SafeDirection
{
	Lower,
	Higher,
}

/// <summary>A number the system works out for a room, with the rules that keep it from starving the room of heat.</summary>
public sealed record LearntNumber
{
	public required double StartingValue { get; init; }

	public required SafeDirection Safe { get; init; }

	public double? Ceiling { get; init; }

	public double? Floor { get; init; }

	public double SystemValue { get; init; }

	public double? ValueSetByAPerson { get; init; }

	public int MeasurementCount { get; init; }

	public int AgreementsSoFar { get; init; }

	public DateTimeOffset? LastChangedAt { get; init; }

	public double Value => ValueSetByAPerson ?? SystemValue;

	public bool Measured => MeasurementCount > 0;

	public bool LockedByAPerson => ValueSetByAPerson.HasValue;

	public static LearntNumber Starting(double startingValue, SafeDirection safe, double? ceiling = null, double? floor = null) =>
		new()
		{
			StartingValue = startingValue,
			Safe = safe,
			Ceiling = ceiling,
			Floor = floor,
			SystemValue = startingValue,
		};

	/// <summary>Moves quickly towards the safe end and slowly away from it.</summary>
	public LearntNumber Observe(double fitted, DateTimeOffset at, PlannerDefaults defaults)
	{
		ArgumentNullException.ThrowIfNull(defaults);

		double wanted = WithinBounds(fitted);
		LearntNumber counted = this with { MeasurementCount = MeasurementCount + 1 };

		if (wanted == SystemValue)
		{
			return counted with { AgreementsSoFar = 0 };
		}

		bool towardsSafe = Safe == SafeDirection.Lower ? wanted < SystemValue : wanted > SystemValue;
		if (towardsSafe)
		{
			return counted with { SystemValue = wanted, AgreementsSoFar = 0, LastChangedAt = at };
		}

		int agreements = AgreementsSoFar + 1;
		return agreements < defaults.AgreementsBeforeRelaxing
			? counted with { AgreementsSoFar = agreements }
			: counted with { SystemValue = wanted, AgreementsSoFar = 0, LastChangedAt = at };
	}

	/// <summary>A value a person sets locks the number. The system stops adjusting it and leaves the set value alone.</summary>
	public LearntNumber SetByHand(double value, DateTimeOffset at) =>
		this with { ValueSetByAPerson = WithinBounds(value), LastChangedAt = at };

	/// <summary>Releases the lock, carrying on from the measurements already taken rather than from the starting value.</summary>
	public LearntNumber ClearTheSetValue() => this with { ValueSetByAPerson = null };

	/// <summary>Back to the safe starting value, with the measurements behind it dropped.</summary>
	public LearntNumber Reset(DateTimeOffset at) =>
		this with
		{
			SystemValue = StartingValue,
			MeasurementCount = 0,
			AgreementsSoFar = 0,
			LastChangedAt = at,
		};

	private double WithinBounds(double value)
	{
		double bounded = value;
		if (Ceiling.HasValue)
		{
			bounded = Math.Min(bounded, Ceiling.Value);
		}

		if (Floor.HasValue)
		{
			bounded = Math.Max(bounded, Floor.Value);
		}

		return bounded;
	}
}

/// <summary>The three numbers a room learns, each starting at its cautious value.</summary>
public static class LearntNumbers
{
	/// <summary>Errs slow, so a warm-up starts early. Starts at the hand-set figure and reads as not measured.</summary>
	public static LearntNumber WarmingRate(double handSetDegreesPerHour)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(handSetDegreesPerHour);
		return LearntNumber.Starting(handSetDegreesPerHour, SafeDirection.Lower, floor: 0.01);
	}

	/// <summary>Errs high, so a room fires harder than it strictly needs rather than settling below its target.</summary>
	public static LearntNumber OutdoorHoldingTerm(PlannerDefaults defaults)
	{
		ArgumentNullException.ThrowIfNull(defaults);
		return LearntNumber.Starting(defaults.HoldingTermStartingPerDegree, SafeDirection.Higher, floor: 0.0);
	}

	/// <summary>Errs narrow, so the heat returns sooner. Never widens past its ceiling.</summary>
	public static LearntNumber Band(PlannerDefaults defaults)
	{
		ArgumentNullException.ThrowIfNull(defaults);
		return LearntNumber.Starting(defaults.BandStartingValue, SafeDirection.Lower, ceiling: defaults.BandCeiling, floor: 0.05);
	}

	/// <summary>What the outdoor holding term takes from a fit, which is a margin above it.</summary>
	public static double FromTheHoldingFit(double fitted, PlannerDefaults defaults)
	{
		ArgumentNullException.ThrowIfNull(defaults);
		return fitted * defaults.HoldingTermMargin;
	}
}
