using AdaptiveHeating.Planner;
using Xunit;

namespace AdaptiveHeating.Planner.Tests;

public sealed class InForceRecordTests
{
	private static readonly DateTimeOffset GoodClock = new(2026, 1, 10, 8, 0, 0, TimeSpan.Zero);
	private static readonly DateTimeOffset WrongClock = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
	private static readonly DateTimeOffset NetworkTime = new(2026, 1, 10, 9, 0, 0, TimeSpan.Zero);

	[Fact]
	public void AChangeMadeAgainstAnUnsetClockIsRefusedUntilTheClockIsSetAndThenStands()
	{
		InForceRecord held = InForceRecord.Empty.Receive(
			"stue",
			new Stamped<double>(21.0, Stamp.Certain(GoodClock), ChangeOrigin.SettingsPage));

		Stamped<double> duringTheOutage = new(23.0, Stamp.AgainstAnUnsetClock(WrongClock), ChangeOrigin.Thermostat);
		// The wrong clock reads later than the good one, so only the mark refuses this change.
		MergeOutcome refusedOutcome = held.Merge([new ArrivingTemperature("stue", duringTheOutage)]);

		Assert.Equal(21.0, refusedOutcome.Record.Temperatures["stue"].Value, 6);
		RefusedChange refusal = Assert.Single(refusedOutcome.Refused);
		Assert.Equal("stue", refusal.RoomId);
		Assert.Equal(23.0, refusal.ArrivingTemperature, 6);
		Assert.Equal(21.0, refusal.TemperatureHeld, 6);

		Stamped<double> rewritten = duringTheOutage.WhenTheClockIsSet(NetworkTime);
		Assert.False(rewritten.Stamp.ClockWasUnset);

		MergeOutcome acceptedOutcome = held.Merge([new ArrivingTemperature("stue", rewritten)]);
		Assert.Empty(acceptedOutcome.Refused);
		Assert.Equal(23.0, acceptedOutcome.Record.Temperatures["stue"].Value, 6);
	}

	[Fact]
	public void TwoStampsReadingAlikeResolveInFavourOfTheSettingsPage()
	{
		InForceRecord held = InForceRecord.Empty.Receive(
			"stue",
			new Stamped<double>(21.0, Stamp.Certain(GoodClock), ChangeOrigin.Thermostat));

		InForceRecord afterThePage = held.Receive(
			"stue",
			new Stamped<double>(19.0, Stamp.Certain(GoodClock), ChangeOrigin.SettingsPage));

		Assert.Equal(19.0, afterThePage.Temperatures["stue"].Value, 6);

		InForceRecord afterTheCard = afterThePage.Receive(
			"stue",
			new Stamped<double>(24.0, Stamp.Certain(GoodClock), ChangeOrigin.Thermostat));

		Assert.Equal(19.0, afterTheCard.Temperatures["stue"].Value, 6);
	}
}
