using AdaptiveHeating.AddOn.Settings;
using AdaptiveHeating.Page.Settings;
using AdaptiveHeating.Planner;

using Xunit;

using ChangeOrigin = AdaptiveHeating.Planner.ChangeOrigin;

namespace AdaptiveHeating.Page.Tests;

/// <summary>
/// The most recent change wins, and a change made while the box did not know the time never displaces one
/// made when it did. Getting either backwards silently undoes what somebody set.
/// </summary>
public sealed class StampsDecideTests
{
	[Fact]
	public void A_newer_change_is_written_and_an_older_one_is_refused()
	{
		InMemorySettingsStore store = new(ACabin.WithOneRoom());

		SaveOutcome later = store.SaveModeStamps(ACabin.RoomId, [HeatingMode.Home],
			Stamp.Certain(ACabin.Typed.AddMinutes(10)));

		SaveOutcome earlier = store.SaveModeStamps(ACabin.RoomId, [HeatingMode.Home],
			Stamp.Certain(ACabin.Typed.AddMinutes(5)));

		Assert.True(later.Written);
		Assert.False(earlier.Written);
		Assert.Equal(ACabin.Typed.AddMinutes(10), store.Read().Rooms[0].ModeStamps[HeatingMode.Home].Stamp.SetAt);
	}

	[Fact]
	public void A_change_made_against_an_unset_clock_loses_however_late_it_reads()
	{
		InMemorySettingsStore store = new(ACabin.WithOneRoom());

		// Years ahead of the good stamp already held, and it still loses.
		SaveOutcome duringTheOutage = store.SaveModeStamps(ACabin.RoomId, [HeatingMode.Home],
			Stamp.AgainstAnUnsetClock(ACabin.Typed.AddYears(3)));

		Assert.False(duringTheOutage.Written);
		Assert.Equal(ACabin.Typed, store.Read().Rooms[0].ModeStamps[HeatingMode.Home].Stamp.SetAt);
	}

	[Fact]
	public void A_stamp_is_marked_until_the_clock_can_have_been_set_and_the_moment_it_can_is_announced()
	{
		DateTimeOffset built = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
		ClockUnderTest clock = new(new DateTimeOffset(2016, 1, 1, 0, 0, 0, TimeSpan.Zero));
		NetworkSetClock watcher = new(clock, built);

		List<DateTimeOffset> announced = [];
		watcher.ClockWasSet += announced.Add;

		Assert.True(watcher.Take().ClockWasUnset);
		Assert.Empty(announced);

		clock.MoveTo(built.AddDays(20));

		Assert.False(watcher.Take().ClockWasUnset);
		Assert.Equal([built.AddDays(20)], announced);

		// Said once. A house is not told twice that its clock is now believable.
		clock.MoveTo(built.AddDays(21));
		Assert.False(watcher.Take().ClockWasUnset);
		Assert.Single(announced);
	}

	[Fact]
	public void A_change_made_during_an_outage_takes_the_moment_the_clock_was_set()
	{
		HeatingDocument held = ACabin.WithOneRoom();
		DateTimeOffset outage = new(2016, 1, 1, 0, 0, 0, TimeSpan.Zero);

		held = held with
		{
			Rooms =
			[
				held.Rooms[0] with
				{
					ModeStamps = new Dictionary<HeatingMode, ModeStamp>
					{
						[HeatingMode.Home] = new(Stamp.AgainstAnUnsetClock(outage), ChangeOrigin.Thermostat)
					}
				}
			]
		};

		HeatingDocument rewritten = held.WhenTheClockIsSet(ACabin.Typed);
		ModeStamp after = rewritten.Rooms[0].ModeStamps[HeatingMode.Home];

		Assert.False(after.Stamp.ClockWasUnset);
		Assert.Equal(ACabin.Typed, after.Stamp.SetAt);
	}
}
