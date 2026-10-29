using System.Globalization;
using System.Text.Json;

using AdaptiveHeating.AddOn.Driving;
using AdaptiveHeating.AddOn.Integration;
using AdaptiveHeating.AddOn.Settings;
using AdaptiveHeating.Planner;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>
/// The outdoor temperature drives how hard every room works, so where it comes from when the sensors fail is what
/// a household feels. One or more sensors are chosen, and the forecast takes over when all of them are out.
/// </summary>
public sealed class TheOutdoorReadingTests
{
	private static readonly DateTimeOffset Morning = new(2026, 9, 25, 7, 0, 0, TimeSpan.Zero);

	[Fact]
	public void The_forecast_takes_over_when_every_chosen_sensor_fails()
	{
		PlannerDefaults defaults = PlannerDefaults.Standard;

		// The control: both sensors reported a moment ago, so the reading is theirs and the forecast is not read.
		OutdoorReading fromSensors = TheOutdoorTemperature.Read(
			States(
				Sensor(ACabin.OutdoorSensor, -4.0, Morning.AddMinutes(-2), Morning.AddMinutes(-2)),
				Sensor(ACabin.SecondOutdoorSensor, -4.4, Morning.AddMinutes(-3), Morning.AddMinutes(-3)),
				Forecast(2.0)),
			ACabin.Outdoor,
			Morning,
			defaults);

		Assert.Equal(OutdoorFrom.Sensors, fromSensors.From);
		Assert.Equal(-4.2, fromSensors.Temperature!.Value, precision: 6);

		// Both gone quiet for longer than the house's stale-after setting, which is what a dead battery looks like.
		DateTimeOffset longAgo = Morning - TimeSpan.FromMinutes(ACabin.Outdoor.StaleAfterMinutes) - TimeSpan.FromMinutes(5);

		OutdoorReading fromForecast = TheOutdoorTemperature.Read(
			States(
				Sensor(ACabin.OutdoorSensor, -4.0, longAgo, longAgo),
				Sensor(ACabin.SecondOutdoorSensor, -4.4, longAgo, longAgo),
				Forecast(2.0)),
			ACabin.Outdoor,
			Morning,
			defaults);

		Assert.Equal(OutdoorFrom.Forecast, fromForecast.From);
		Assert.Equal(2.0, fromForecast.Temperature);

		// Both sensors are named as left out, and the reading is not quietly one of theirs.
		Assert.Equal(2, fromForecast.Sensors.LeftOut.Count());
		Assert.All(fromForecast.Sensors.LeftOut, verdict => Assert.Equal(SensorExclusion.Quiet, verdict.Excluded));
		Assert.NotEqual(-4.2, fromForecast.Temperature!.Value);

		// With no forecast to fall back on the answer is nothing, never the last sensor value.
		OutdoorReading nothing = TheOutdoorTemperature.Read(
			States(Sensor(ACabin.OutdoorSensor, -4.0, longAgo, longAgo)),
			new OutdoorChoice([ACabin.OutdoorSensor], Forecast: null),
			Morning,
			defaults);

		Assert.Equal(OutdoorFrom.Nothing, nothing.From);
		Assert.Null(nothing.Temperature);
	}

	[Fact]
	public void The_house_says_how_long_an_outdoor_sensor_may_stay_silent()
	{
		DateTimeOffset ninetyMinutesAgo = Morning.AddMinutes(-90);
		string[] states = [Sensor(ACabin.OutdoorSensor, -4.0, ninetyMinutesAgo, ninetyMinutesAgo), Forecast(2.0)];
		OutdoorChoice chosen = new([ACabin.OutdoorSensor], ACabin.Forecast);

		Assert.Equal(OutdoorFrom.Sensors, Read(chosen with { StaleAfterMinutes = 120 }).From);
		Assert.Equal(OutdoorFrom.Forecast, Read(chosen with { StaleAfterMinutes = 60 }).From);

		// Zero switches the rule off, so even a day of silence is believed.
		DateTimeOffset aDayAgo = Morning.AddDays(-1);
		OutdoorReading off = TheOutdoorTemperature.Read(
			States(Sensor(ACabin.OutdoorSensor, -4.0, aDayAgo, Morning.AddMinutes(-10)), Forecast(2.0)),
			chosen with { StaleAfterMinutes = 0 },
			Morning,
			PlannerDefaults.Standard);

		Assert.Equal(OutdoorFrom.Sensors, off.From);

		OutdoorReading Read(OutdoorChoice choice) =>
			TheOutdoorTemperature.Read(States(states), choice, Morning, PlannerDefaults.Standard);
	}

	[Fact]
	public void A_sensor_silent_before_a_core_restart_stays_out_after_it()
	{
		string directory = Directory.CreateTempSubdirectory("heating-last-seen-").FullName;
		string document = Path.Combine(directory, "heating.json");
		DateTimeOffset lastHeard = Morning.AddHours(-5);

		try
		{
			// Before the restart: the cache last heard the sensor five hours ago.
			LastSeenDocument before = new() { Bucket = "temperature", SavedAt = Morning.AddMinutes(-30) };
			before.Entities[ACabin.OutdoorSensor] = new LastSeenEntry(lastHeard, lastHeard.AddDays(-3));
			Assert.True(new LastSeenStore(document, NullLogger<LastSeenStore>.Instance).TrySave("temperature", before));

			// Lighting's relocated layout: a last-seen folder beside the document, and nothing else beside it.
			Assert.Single(Directory.GetFiles(Path.Combine(directory, LastSeenStore.FolderName)));
			Assert.Empty(Directory.GetFiles(directory));

			// After it: Home Assistant stamps every entity with the restart, so on its own fields the sensor is fresh.
			LoadedCache after = new(new LastSeenStore(document, NullLogger<LastSeenStore>.Instance).Load());
			string[] restarted = [Sensor(ACabin.OutdoorSensor, -4.0, Morning.AddMinutes(-1), Morning.AddMinutes(-1)), Forecast(2.0)];

			Assert.Equal(OutdoorFrom.Sensors, TheOutdoorTemperature.Read(States(restarted), ACabin.Outdoor, Morning, PlannerDefaults.Standard).From);

			OutdoorReading reading = TheOutdoorTemperature.Read(States(restarted), ACabin.Outdoor, Morning, PlannerDefaults.Standard, after);

			Assert.Equal(OutdoorFrom.Forecast, reading.From);
			Assert.Equal(SensorExclusion.Quiet, Assert.Single(reading.Sensors.LeftOut).Excluded);
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	// The tracker's answers as a restart's load leaves them, with nothing sampled since.
	private sealed class LoadedCache(LastSeenCacheLoad load) : IEntityLastSeen
	{
		public DateTimeOffset? HomeAssistantStartedUtc => load.HomeAssistantStarted;

		public int TrackedCount => load.Entities.Count;

		public DateTimeOffset? LastSeenUtc(string entityId) =>
			load.Entities.TryGetValue(entityId, out LoadedEntity? loaded) ? loaded.Entry.LastSeen : null;

		public TimeSpan? SilenceOf(string entityId) => Morning - LastSeenUtc(entityId);

		public bool HasBeenSilentFor(string entityId, TimeSpan threshold) => SilenceOf(entityId) > threshold;
	}

	private static List<EntityState> States(params string[] entities) =>
		JsonSerializer.Deserialize<List<EntityState>>($"[{string.Join(",", entities)}]")!;

	private static string Sensor(string entityId, double temperature, DateTimeOffset reportedAt, DateTimeOffset movedAt) =>
		$$"""
		{
			"entity_id": "{{entityId}}",
			"state": "{{Invariant(temperature)}}",
			"last_updated": "{{Moment(reportedAt)}}",
			"last_changed": "{{Moment(movedAt)}}",
			"attributes": { "friendly_name": "Outside", "unit_of_measurement": "°C" }
		}
		""";

	private static string Forecast(double temperature) =>
		$$"""
		{
			"entity_id": "{{ACabin.Forecast}}",
			"state": "cloudy",
			"attributes": { "friendly_name": "Cabin", "temperature": {{Invariant(temperature)}} }
		}
		""";

	private static string Moment(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);

	[Fact]
	public void A_forecast_answer_and_an_attribute_reading_each_reduce_to_one_number()
	{
		JsonElement answer = JsonSerializer.Deserialize<JsonElement>(
			$$"""
			{
				"{{ACabin.Forecast}}": {
					"forecast": [
						{ "datetime": "{{Moment(Morning.AddHours(1))}}", "temperature": -3.0 },
						{ "datetime": "{{Moment(Morning.AddHours(4))}}", "temperature": -7.5 },
						{ "datetime": "{{Moment(Morning.AddHours(20))}}", "temperature": -19.0 }
					]
				}
			}
			""");

		// The lowest hour inside the window a warm-up can be planning over, and the hour beyond it is left out.
		Assert.Equal(
			-7.5,
			TheOutdoorTemperature.HoursAhead(answer, ACabin.Forecast, Morning, TheOutdoorTemperature.HoursAheadThatMatter));

		// An answer for another entity reduces to nothing rather than to somebody else's weather.
		Assert.Null(
			TheOutdoorTemperature.HoursAhead(answer, "weather.somewhere_else", Morning, TheOutdoorTemperature.HoursAheadThatMatter));

		// The second source is the weather entity's own attribute, which is a reading of now.
		OutdoorReading reading = TheOutdoorTemperature.Read(
			States(Forecast(-1.5)),
			new OutdoorChoice([ACabin.OutdoorSensor], ACabin.Forecast),
			Morning,
			PlannerDefaults.Standard);

		Assert.Equal(OutdoorFrom.Forecast, reading.From);
		Assert.Equal(-1.5, reading.Temperature);
	}

	// A temperature written the Norwegian way is not a number to a JSON reader, and a state is a machine value.
	private static string Invariant(double value) => value.ToString(CultureInfo.InvariantCulture);
}
