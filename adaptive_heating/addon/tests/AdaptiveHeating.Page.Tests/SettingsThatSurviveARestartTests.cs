using System.Text.Json.Nodes;

using AdaptiveHeating.AddOn.Settings;
using AdaptiveHeating.Page.Settings;
using AdaptiveHeating.Planner;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

using ChangeOrigin = AdaptiveHeating.Planner.ChangeOrigin;

namespace AdaptiveHeating.Page.Tests;

/// <summary>
/// What a person typed has to still be there after the add-on restarts. Losing it is silent, reaches a
/// person at the worst moment, and reads as the page having ignored them.
/// </summary>
public sealed class SettingsThatSurviveARestartTests
{
	[Fact]
	public void A_saved_stamp_is_there_after_a_restart()
	{
		string directory = ACabin.ScratchDirectory();
		ClockUnderTest clock = new(ACabin.Typed);

		using (StateStoreRegistry first = Registry(directory))
		{
			DurableSettingsStore store = DurableSettingsStore.Open(first, clock, ACabin.WithOneRoom(), NullLogger.Instance);

			SaveOutcome outcome = store.SaveModeStamps(ACabin.RoomId, [HeatingMode.Home],
				Stamp.Certain(ACabin.Typed.AddMinutes(5)));

			Assert.True(outcome.Written);
		}

		using StateStoreRegistry second = Registry(directory);
		DurableSettingsStore restarted = DurableSettingsStore.Open(second, clock, ACabin.WithOneRoom(), NullLogger.Instance);

		// The stamp is what decides the next contest, so it has to cross the restart; the value is in the document.
		Assert.Equal(ACabin.Typed.AddMinutes(5), restarted.Read().Rooms[0].ModeStamps[HeatingMode.Home].Stamp.SetAt);
	}

	[Fact]
	public void The_rises_a_room_made_and_its_on_or_off_cells_are_there_after_a_restart()
	{
		string directory = ACabin.ScratchDirectory();
		ClockUnderTest clock = new(ACabin.Typed);
		HeatingDocument held = ACabin.WithOneRoom();

		RoomSlice measured = held.Rooms[0] with
		{
			NoReadingAtAll = true,
			ModeSwitches = RoomModeSwitches.Starting(Stamp.Certain(ACabin.Typed)),
			RateMeasurements = [new WarmingRateMeasurement(-5.0, 1.25, ACabin.Typed)],
		};

		using (StateStoreRegistry first = Registry(directory))
		{
			DurableSettingsStore store = DurableSettingsStore.Open(first, clock, held, NullLogger.Instance);
			store.Receive(held with { Rooms = [measured] });
		}

		using StateStoreRegistry second = Registry(directory);
		RoomSlice back = DurableSettingsStore.Open(second, clock, held, NullLogger.Instance).Read().Rooms[0];

		// A winter of measurements is what the warming rate rests on, and a mode's on-or-off cell is the
		// only instruction a room with no reading ever gets.
		Assert.True(back.NoReadingAtAll);
		Assert.True(back.ModeSwitches[HeatingMode.Away].Value);
		Assert.False(back.ModeSwitches[HeatingMode.Home].Value);
		Assert.Equal(1.25, Assert.Single(back.RateMeasurements).DegreesPerHour);
		Assert.Equal(-5.0, back.RateMeasurements[0].OutdoorTemperature);
	}

	[Fact]
	public void The_two_calendars_are_chosen_by_entity_id_and_are_there_after_a_restart()
	{
		string directory = ACabin.ScratchDirectory();
		ClockUnderTest clock = new(ACabin.Typed);

		using (StateStoreRegistry first = Registry(directory))
		{
			DurableSettingsStore store = DurableSettingsStore.Open(first, clock, ACabin.WithOneRoom(), NullLogger.Instance);

			Assert.True(store.SaveCalendars("calendar.adaptiveheating_plan", "calendar.adaptiveheating_log").Written);
		}

		using StateStoreRegistry second = Registry(directory);
		HeatingDocument restored = DurableSettingsStore
			.Open(second, clock, ACabin.WithOneRoom(), NullLogger.Instance)
			.Read();

		Assert.Equal("calendar.adaptiveheating_plan", restored.ArrivalCalendar);
		Assert.Equal("calendar.adaptiveheating_log", restored.RecordCalendar);

		// The list of calendars on offer is a reading of the house, so it is not written and comes back empty.
		Assert.Empty(restored.CalendarsOffered);
	}

	[Fact]
	public void A_field_the_file_carries_and_this_build_does_not_know_is_ignored()
	{
		string directory = ACabin.ScratchDirectory();
		ClockUnderTest clock = new(ACabin.Typed);

		using (StateStoreRegistry first = Registry(directory))
		{
			DurableSettingsStore store = DurableSettingsStore.Open(first, clock, ACabin.WithOneRoom(), NullLogger.Instance);

			Assert.True(store.SetLearntByHand(ACabin.RoomId, LearntKind.Band, 0.7).Written);

			AddAFieldNobodyKnows(store.FilePath);
		}

		using StateStoreRegistry second = Registry(directory);
		DurableSettingsStore restarted = DurableSettingsStore.Open(second, clock, ACabin.WithOneRoom(), NullLogger.Instance);

		// Read, not refused: a house whose two halves are different ages must keep working.
		Assert.Equal(0.7, restarted.Read().Rooms[0].Learnt.Band.Value);
	}

	[Fact]
	public void The_rewrite_the_clock_being_set_forces_survives_a_restart()
	{
		string directory = ACabin.ScratchDirectory();
		ClockUnderTest clock = new(ACabin.Typed);
		DateTimeOffset duringTheOutage = new(2016, 1, 1, 0, 0, 0, TimeSpan.Zero);

		HeatingDocument setWhileTheClockWasWrong = ACabin.WithOneRoom();
		setWhileTheClockWasWrong = setWhileTheClockWasWrong with
		{
			Rooms =
			[
				setWhileTheClockWasWrong.Rooms[0] with
				{
					ModeStamps = new Dictionary<HeatingMode, ModeStamp>
					{
						[HeatingMode.Home] = new(Stamp.AgainstAnUnsetClock(duringTheOutage), ChangeOrigin.SettingsPage)
					}
				}
			]
		};

		using (StateStoreRegistry first = Registry(directory))
		{
			DurableSettingsStore store = DurableSettingsStore.Open(first, clock, setWhileTheClockWasWrong, NullLogger.Instance);

			store.WhenTheClockIsSet(ACabin.Typed.AddHours(1));
		}

		using StateStoreRegistry second = Registry(directory);
		DurableSettingsStore restarted = DurableSettingsStore.Open(second, clock, ACabin.WithOneRoom(), NullLogger.Instance);

		ModeStamp held = restarted.Read().Rooms[0].ModeStamps[HeatingMode.Home];

		Assert.False(held.Stamp.ClockWasUnset);
		Assert.Equal(ACabin.Typed.AddHours(1), held.Stamp.SetAt);
	}

	private static StateStoreRegistry Registry(string directory) =>
		new(Path.Combine(directory, "heating.json"), NullLogger<StateStoreRegistry>.Instance);

	// A build that knew a field this one does not would have written it beside the ones this one reads.
	private static void AddAFieldNobodyKnows(string path)
	{
		JsonNode file = JsonNode.Parse(File.ReadAllText(path))!;

		file["somethingFromALaterBuild"] = "a value this build has never heard of";
		file["Settings"]!["Rooms"]![0]!["anotherOne"] = 42;

		File.WriteAllText(path, file.ToJsonString());
	}
}
