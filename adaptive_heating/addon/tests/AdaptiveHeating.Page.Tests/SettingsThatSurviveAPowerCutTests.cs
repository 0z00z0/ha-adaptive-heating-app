using AdaptiveHeating.AddOn.Settings;
using AdaptiveHeating.Page.Settings;
using AdaptiveHeating.Planner;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>A cabin back from a power cut keeps whatever time its hardware held, which can read hours behind the
/// settings file. Discarding the file then empties every temperature a person typed.</summary>
public sealed class SettingsThatSurviveAPowerCutTests
{
	[Fact]
	public void A_file_saved_an_hour_ahead_of_the_clock_loads_intact()
	{
		string directory = ACabin.ScratchDirectory();
		ClockUnderTest clock = new(ACabin.Typed);

		using (StateStoreRegistry before = Registry(directory))
		{
			DurableSettingsStore store = DurableSettingsStore.Open(before, clock, ACabin.WithOneRoom(), NullLogger.Instance);

			Assert.True(store
				.SaveModeStamps(ACabin.RoomId, [HeatingMode.Home],
					Stamp.Certain(ACabin.Typed.AddMinutes(5)))
				.Written);
		}

		// The box comes back on a hardware clock an hour behind, so the file reads as saved in the future.
		clock.MoveTo(ACabin.Typed.AddHours(-1));

		using StateStoreRegistry after = Registry(directory);
		DurableSettingsStore restarted = DurableSettingsStore.Open(after, clock, ACabin.WithOneRoom(), NullLogger.Instance);

		Assert.Equal(ACabin.Typed.AddMinutes(5), restarted.Read().Rooms[0].ModeStamps[HeatingMode.Home].Stamp.SetAt);

		// The other direction: the fallback document holds the stamp the cabin was typed at, so reading it would be
		// a discarded file rather than a restored one.
		Assert.NotEqual(ACabin.Typed, restarted.Read().Rooms[0].ModeStamps[HeatingMode.Home].Stamp.SetAt);
	}

	[Fact]
	public void A_write_the_box_refuses_is_reported_as_refused_and_says_why()
	{
		string directory = ACabin.ScratchDirectory();
		ClockUnderTest clock = new(ACabin.Typed);

		using StateStoreRegistry registry = Registry(directory);
		DurableSettingsStore store = DurableSettingsStore.Open(registry, clock, ACabin.WithOneRoom(), NullLogger.Instance);

		// A directory where the file belongs: the rename at the end of the write cannot land, which is the shape
		// a full disk and a read-only mount both take.
		Directory.CreateDirectory(store.FilePath);

		SaveOutcome outcome = store.SaveModeStamps(ACabin.RoomId, [HeatingMode.Home],
			Stamp.Certain(ACabin.Typed.AddMinutes(5)));

		Assert.False(outcome.Written);
		Assert.NotEqual("", outcome.Message);
	}

	[Fact]
	public void A_write_that_lands_is_reported_as_written_and_is_on_the_disk()
	{
		string directory = ACabin.ScratchDirectory();
		ClockUnderTest clock = new(ACabin.Typed);

		using StateStoreRegistry registry = Registry(directory);
		DurableSettingsStore store = DurableSettingsStore.Open(registry, clock, ACabin.WithOneRoom(), NullLogger.Instance);

		SaveOutcome outcome = store.SaveModeStamps(ACabin.RoomId, [HeatingMode.Home],
			Stamp.Certain(ACabin.Typed.AddMinutes(5)));

		Assert.True(outcome.Written);
		Assert.True(File.Exists(store.FilePath), "A save reported as written left no file behind.");
	}

	private static StateStoreRegistry Registry(string directory) =>
		new(Path.Combine(directory, "heating.json"), NullLogger<StateStoreRegistry>.Instance);
}
