namespace AdaptiveHeating.Planner;

/// <summary>When a warm-up has to start, and what it reaches where it cannot make the deadline.</summary>
public sealed record WarmUpPlan(
	double TemperatureWanted,
	double PlanningRate,
	TimeSpan PlannedDuration,
	TimeSpan StartsBeforeTheDeadline,
	bool ReachesItInTime,
	double TemperatureItReaches,
	TimeSpan ShortBy)
{
	public bool NothingToDo => PlannedDuration <= TimeSpan.Zero;

	/// <summary>How early a room behaving as measured arrives, because the plan runs on a fraction of the rate.</summary>
	public TimeSpan SpareIfTheRoomBehavesAsMeasured { get; init; }
}

/// <summary>A room that cannot be at its temperature by the deadline, named with how far short it falls.</summary>
public sealed record CannotReach(string RoomId, double TemperatureWanted, double TemperatureItReaches, TimeSpan ShortBy);

public static class WarmUp
{
	/// <summary>Solves backwards from the deadline on a fraction of the measured rate, so the start is early rather than late.</summary>
	public static WarmUpPlan Plan(
		double roomTemperature,
		double temperatureWanted,
		TimeSpan timeUntilTheDeadline,
		double measuredDegreesPerHour,
		PlannerDefaults defaults)
	{
		ArgumentNullException.ThrowIfNull(defaults);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(measuredDegreesPerHour);

		double rise = temperatureWanted - roomTemperature;
		if (rise <= 0.0)
		{
			return new WarmUpPlan(temperatureWanted, 0.0, TimeSpan.Zero, TimeSpan.Zero, true, roomTemperature, TimeSpan.Zero);
		}

		double planningRate = measuredDegreesPerHour * defaults.WarmUpPlanningFraction;
		TimeSpan plannedDuration = TimeSpan.FromHours(rise / planningRate);
		TimeSpan spare = plannedDuration * (1.0 - defaults.WarmUpPlanningFraction);

		if (plannedDuration <= timeUntilTheDeadline)
		{
			return new WarmUpPlan(temperatureWanted, planningRate, plannedDuration, plannedDuration, true, temperatureWanted, TimeSpan.Zero)
			{
				SpareIfTheRoomBehavesAsMeasured = spare,
			};
		}

		// A room that cannot make it starts as early as it is allowed to and heats anyway.
		double reached = roomTemperature + (planningRate * timeUntilTheDeadline.TotalHours);
		return new WarmUpPlan(temperatureWanted, planningRate, plannedDuration, timeUntilTheDeadline, false, reached, plannedDuration - timeUntilTheDeadline)
		{
			SpareIfTheRoomBehavesAsMeasured = TimeSpan.Zero,
		};
	}

	/// <summary>
	/// The warm-up to ask for now, or <c>null</c> where the planned start is still ahead or the room needs no rise.
	/// </summary>
	/// <remarks>The integration solves backwards again from the rate it holds, so asking at the planned start is
	/// what makes the two agree about when the heater comes on.</remarks>
	public static WarmUpPlan? DueNow(
		double roomTemperature,
		double temperatureWanted,
		DateTimeOffset deadline,
		DateTimeOffset now,
		double estimatedDegreesPerHour,
		PlannerDefaults defaults)
	{
		ArgumentNullException.ThrowIfNull(defaults);

		if (deadline <= now)
			return null;

		WarmUpPlan plan = Plan(roomTemperature, temperatureWanted, deadline - now, estimatedDegreesPerHour, defaults);

		return plan.NothingToDo || deadline - plan.StartsBeforeTheDeadline > now ? null : plan;
	}

	public static CannotReach? Report(string roomId, WarmUpPlan plan)
	{
		ArgumentNullException.ThrowIfNull(plan);
		return plan.ReachesItInTime
			? null
			: new CannotReach(roomId, plan.TemperatureWanted, plan.TemperatureItReaches, plan.ShortBy);
	}
}
