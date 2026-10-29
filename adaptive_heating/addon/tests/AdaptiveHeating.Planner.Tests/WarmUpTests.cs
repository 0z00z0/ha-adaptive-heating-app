using AdaptiveHeating.Planner;
using Xunit;

namespace AdaptiveHeating.Planner.Tests;

public sealed class WarmUpTests
{
	private static readonly PlannerDefaults Defaults = PlannerDefaults.Standard;

	[Fact]
	public void AWarmUpIsPlannedOnFourFifthsOfTheMeasuredRateSoTheRoomArrivesEarly()
	{
		WarmUpPlan plan = WarmUp.Plan(
			roomTemperature: 10.0,
			temperatureWanted: 15.0,
			timeUntilTheDeadline: TimeSpan.FromHours(24),
			measuredDegreesPerHour: 1.0,
			Defaults);

		Assert.True(plan.ReachesItInTime);
		Assert.Equal(0.8, plan.PlanningRate, 6);
		Assert.Equal(6.25, plan.PlannedDuration.TotalHours, 6);
		Assert.Equal(6.25, plan.StartsBeforeTheDeadline.TotalHours, 6);

		// A room behaving as measured takes 5 hours of the 6.25 planned, so it arrives a fifth of the
		// planned time early, which is a quarter of the time the rise actually takes.
		double hoursAtTheMeasuredRate = (15.0 - 10.0) / 1.0;
		Assert.Equal(1.25, plan.SpareIfTheRoomBehavesAsMeasured.TotalHours, 6);
		Assert.Equal(0.25, plan.SpareIfTheRoomBehavesAsMeasured.TotalHours / hoursAtTheMeasuredRate, 6);
	}

	[Fact]
	public void TheWarmUpIsAskedForAtThePlannedStartAndNotBeforeIt()
	{
		DateTimeOffset deadline = new(2026, 1, 15, 18, 0, 0, TimeSpan.Zero);

		// Four fifths of two degrees an hour is 1.6, so four degrees is planned as two and a half hours.
		Assert.Null(WarmUp.DueNow(17.0, 21.0, deadline, deadline.AddHours(-3), 2.0, Defaults));

		WarmUpPlan? due = WarmUp.DueNow(17.0, 21.0, deadline, deadline.AddHours(-2.5), 2.0, Defaults);
		Assert.NotNull(due);
		Assert.Equal(2.5, due.PlannedDuration.TotalHours, 6);

		// A room already at the temperature needs no rise, and a deadline that has gone is no deadline.
		Assert.Null(WarmUp.DueNow(21.5, 21.0, deadline, deadline.AddHours(-2.5), 2.0, Defaults));
		Assert.Null(WarmUp.DueNow(17.0, 21.0, deadline, deadline, 2.0, Defaults));
	}

	[Fact]
	public void ARoomThatCannotMakeTheDeadlineStartsAtOnceAndIsNamedWithHowFarShortItFalls()
	{
		WarmUpPlan plan = WarmUp.Plan(
			roomTemperature: 5.0,
			temperatureWanted: 15.0,
			timeUntilTheDeadline: TimeSpan.FromHours(4),
			measuredDegreesPerHour: 1.0,
			Defaults);

		Assert.False(plan.ReachesItInTime);
		Assert.Equal(4.0, plan.StartsBeforeTheDeadline.TotalHours, 6);
		Assert.Equal(8.2, plan.TemperatureItReaches, 6);
		Assert.Equal(8.5, plan.ShortBy.TotalHours, 6);

		CannotReach? report = WarmUp.Report("stue", plan);
		Assert.NotNull(report);
		Assert.Equal("stue", report.RoomId);
		Assert.Equal(15.0, report.TemperatureWanted, 6);
		Assert.Equal(8.2, report.TemperatureItReaches, 6);
		Assert.Null(WarmUp.Report("stue", WarmUp.Plan(10.0, 15.0, TimeSpan.FromHours(24), 1.0, Defaults)));
	}
}
