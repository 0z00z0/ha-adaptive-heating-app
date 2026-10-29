using Adaptive.Settings;

using AdaptiveHeating.AddOn.Reporting;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Ha;
using AdaptiveLighting.Web.Presentation;
using AdaptiveLighting.Web.Services;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>The dashboard draws the room event as Home Assistant hands it back, so the two halves must agree on every name.</summary>
public sealed class TheDashboardReadsTheRoomEventBackTests
{
	[Fact]
	public void A_published_room_reads_back_as_the_same_snapshot()
	{
		AHouseUnderTest house = new();
		using AreaSnapshotCache cache = new(NullLogger<AreaSnapshotCache>.Instance);
		cache.CameUp(house);

		DateTimeOffset at = new(2026, 10, 26, 7, 30, 0, TimeSpan.FromHours(1));
		AreaSnapshot sent = new(
			"Living room", AreaState.OverriddenOn, TransitionReason.ManualOn, ModeKind.Away, true, "Morning", 21.5, 20.25,
			at, at.AddMinutes(-1), at.AddHours(2), at, "Away", "stue", AutoOnBlock.EntityOn, "input_boolean.fire",
			true, false, new ForcedMode(ModeKind.Away, "Away", ModeForceSource.WhileEntityOn, "input_boolean.trip", "on"),
			true, "input_boolean.guests", "the mode", 1, 2, "Alex", at.AddMinutes(-3),
			[new SensorBattery("sensor.thermometer", 12)], "Away");

		new HaStatePublisher(() => house, NullLogger.Instance).Publish(sent);

		AreaSnapshot read = Assert.Single(cache.Snapshots);
		Assert.Equal(sent with { LowBatteries = null }, read with { LowBatteries = null });
		Assert.Equal(sent.LowBatteries, read.LowBatteries);
	}

	[Fact]
	public void Every_kind_of_report_has_a_chip_on_the_activity_page()
	{
		foreach (WhatHappened what in Enum.GetValues<WhatHappened>())
		{
			ActivityCategory category = ReportsWhatHappened.CategoryOf(what);

			Assert.NotEqual(ActivityCategory.None, category);
			Assert.Equal(category, category & ActivityView.AllCategories);
		}
	}
}
