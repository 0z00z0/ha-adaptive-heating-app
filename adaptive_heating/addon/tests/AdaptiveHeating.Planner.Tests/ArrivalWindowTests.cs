using Xunit;

namespace AdaptiveHeating.Planner.Tests;

/// <summary>
/// A cabin holds more than one visit ahead of it, and the nearest one is what a warm-up has to be planned
/// against. An arrival already reached is no deadline at all.
/// </summary>
public sealed class ArrivalWindowTests
{
	private static readonly DateTimeOffset Now = new(2026, 9, 24, 7, 0, 0, TimeSpan.Zero);

	[Fact]
	public void The_nearest_arrival_still_ahead_is_the_deadline()
	{
		ArrivalWindow window = ArrivalWindow.Of(
			Now,
			Now + ArrivalWindow.Length,
			[Now.AddDays(5), Now.AddDays(2), Now.AddDays(9)]);

		Assert.Equal([Now.AddDays(2), Now.AddDays(5), Now.AddDays(9)], window.Arrivals);
		Assert.Equal(Now.AddDays(2), window.Next(Now));

		// Past the first two, the third is what is left to plan against.
		Assert.Equal(Now.AddDays(9), window.Next(Now.AddDays(6)));
	}

	[Fact]
	public void An_arrival_already_reached_is_no_deadline()
	{
		ArrivalWindow window = ArrivalWindow.Of(Now, Now + ArrivalWindow.Length, [Now.AddHours(-1), Now]);

		// A warm-up planned backwards from a moment that has come round would have no time to run.
		Assert.Null(window.Next(Now));
	}

	[Fact]
	public void No_reading_is_not_the_same_as_a_calendar_holding_nothing()
	{
		Assert.False(ArrivalWindow.Unread.WasRead);
		Assert.True(ArrivalWindow.Unread.IsStaleAt(Now));

		ArrivalWindow read = ArrivalWindow.Of(Now, Now + ArrivalWindow.Length, []);

		Assert.True(read.WasRead);
		Assert.Null(read.Next(Now));
	}

	[Fact]
	public void A_reading_stands_for_its_own_stretch_and_is_stale_after_it()
	{
		ArrivalWindow window = ArrivalWindow.Of(Now, Now + ArrivalWindow.Length, [Now.AddDays(1)]);

		Assert.False(window.IsStaleAt(Now + ArrivalWindow.StandsFor - TimeSpan.FromSeconds(1)));
		Assert.True(window.IsStaleAt(Now + ArrivalWindow.StandsFor));
	}

	[Fact]
	public void The_window_is_longer_than_the_longest_warm_up_a_room_can_plan()
	{
		// The lowest rate a room begins on, planned at four fifths of it, against the biggest rise a cabin sees.
		double planningRate = 0.5 * PlannerDefaults.Standard.WarmUpPlanningFraction;
		TimeSpan longest = TimeSpan.FromHours((21.0 - 5.0) / planningRate);

		Assert.True(ArrivalWindow.Length > longest, "The window has to reach past the longest warm-up.");
	}
}
