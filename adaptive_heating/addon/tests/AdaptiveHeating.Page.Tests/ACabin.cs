using AdaptiveHeating.AddOn.Settings;
using AdaptiveHeating.Page.Presentation;
using AdaptiveHeating.Page.Settings;
using AdaptiveHeating.Planner;

using ChangeOrigin = AdaptiveHeating.Planner.ChangeOrigin;

namespace AdaptiveHeating.Page.Tests;

/// <summary>One room and one period, which is the smallest settings document anything can be saved against.</summary>
internal static class ACabin
{
	public const string RoomId = "kitchen";
	public const string Thermostat = "climate.kitchen";

	/// <summary>The outdoor sensors a person chose on the add-on's own configuration page.</summary>
	public const string OutdoorSensor = "sensor.outdoor_north";

	public const string SecondOutdoorSensor = "sensor.outdoor_south";

	/// <summary>The weather entity the rooms run on when every chosen outdoor sensor fails the check.</summary>
	public const string Forecast = "weather.cabin";

	public static readonly DateTimeOffset Typed = new(2026, 9, 24, 8, 0, 0, TimeSpan.Zero);

	/// <summary>The outdoor sensors and the forecast, as the add-on's configuration form hands them over.</summary>
	public static OutdoorChoice Outdoor { get; } = new([OutdoorSensor, SecondOutdoorSensor], Forecast);

	public static HeatingDocument WithOneRoom(double away = 4, double home = 20) =>
		new(
			[
				new RoomSlice(
					RoomId,
					"Kitchen",
					RoomSensorSettings.Standard(PlannerDefaults.Standard),
					RoomLearntNumbers.Starting(PlannerDefaults.Standard))
				{
					// One string for both: a test telling the room id from the area id names its own.
					AreaId = RoomId,
					Thermostat = Thermostat,

					// Handed in as a store with no settings document under it holds them.
					ModeTemperatures = new Dictionary<HeatingMode, Stamped<double>>
					{
						[HeatingMode.Away] = Held(away),
						[HeatingMode.Home] = Held(home)
					},
					ModeStamps = new Dictionary<HeatingMode, ModeStamp>
					{
						[HeatingMode.Away] = new(Stamp.Certain(Typed), ChangeOrigin.SettingsPage),
						[HeatingMode.Home] = new(Stamp.Certain(Typed), ChangeOrigin.SettingsPage)
					}
				}
			],
			new HalfVersions(HalfVersions.AddOnVersion, null, Vocabulary.Version));

	public static Stamped<double> Held(double value, DateTimeOffset? at = null) =>
		new(value, Stamp.Certain(at ?? Typed), ChangeOrigin.SettingsPage);

	private static int _scratchDirectories;

	/// <summary>A directory of its own per test, named for the test and the process, so two tests never share a settings file.</summary>
	public static string ScratchDirectory([System.Runtime.CompilerServices.CallerMemberName] string test = "")
	{
		string path = Path.Combine(
			Path.GetTempPath(),
			"adaptive-heating-tests",
			$"process-{Environment.ProcessId}",
			$"{test}-{Interlocked.Increment(ref _scratchDirectories):000}");
		Directory.CreateDirectory(path);

		return path;
	}
}

/// <summary>A clock a test moves by hand.</summary>
internal sealed class ClockUnderTest : TimeProvider
{
	private DateTimeOffset _now;

	public ClockUnderTest(DateTimeOffset now) => _now = now;

	public override DateTimeOffset GetUtcNow() => _now;

	public void MoveTo(DateTimeOffset now) => _now = now;
}
