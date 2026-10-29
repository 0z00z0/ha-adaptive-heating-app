using System.Globalization;
using System.Text.Json;

using Adaptive.Settings;

using AdaptiveHeating.AddOn.Driving;
using AdaptiveHeating.AddOn.Integration;
using AdaptiveHeating.AddOn.Settings;
using AdaptiveHeating.Page.Settings;
using AdaptiveHeating.Planner;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>The setback's floor; the room could reach about -8 °C, so the floor binds and away (9 °C) never does.</summary>
public sealed class TheNightSetbacksFloorTests
{
	private static readonly DateTimeOffset Asleep = new(2026, 10, 20, 23, 0, 0, TimeSpan.Zero);

	[Fact]
	public async Task A_room_with_no_frost_temperature_of_its_own_stops_at_the_houses()
	{
		Assert.Equal(5.0, await SetBackToAsync(new AreaSettings { LowestNightTemperature = 2 }, new AreaConfig()));
	}

	[Fact]
	public async Task A_rooms_own_frost_temperature_wins_over_the_houses()
	{
		Assert.Equal(7.0, await SetBackToAsync(new AreaSettings { LowestNightTemperature = 2 }, new AreaConfig { FrostTemperature = 7 }));
	}

	[Theory]
	[InlineData(false, null)]
	[InlineData(true, false)]
	public async Task With_no_frost_limit_the_setback_goes_below_0_where_the_lowest_night_temperature_allows(bool houseLimit, bool? roomLimit)
	{
		AreaSettings house = new() { LowestNightTemperature = 2, FrostProtection = houseLimit };
		AreaConfig room = new() { LowestNightTemperature = -3, FrostProtection = roomLimit };

		Assert.Equal(-3.0, await SetBackToAsync(house, room));
	}

	[Theory]
	[InlineData(8, 6, 8)]
	[InlineData(4, 7, 7)]
	public async Task The_setback_stops_at_the_higher_of_the_frost_and_the_lowest_night_temperature(double frost, double lowest, double expected)
	{
		Assert.Equal(expected, await SetBackToAsync(new AreaSettings { FrostTemperature = frost, LowestNightTemperature = lowest }, new AreaConfig()));
	}

	[Theory]
	[InlineData(null, null, 15)]
	[InlineData(10.0, null, 10)]
	[InlineData(10.0, 12.0, 12)]
	public async Task The_lowest_night_temperature_is_the_houses_unless_the_room_sets_its_own(double? house, double? room, double expected)
	{
		AreaSettings defaults = new();
		if (house is { } value)
			defaults.LowestNightTemperature = value;

		Assert.Equal(expected, await SetBackToAsync(defaults, new AreaConfig { LowestNightTemperature = room }));
	}

	[Theory]
	[InlineData(8.0, 8.0)]
	[InlineData(4.0, 5.0)]
	public async Task The_setback_stops_at_the_thermostats_own_minimum_where_it_is_the_highest(double minimum, double expected)
	{
		Assert.Equal(expected, await SetBackToAsync(new AreaSettings { LowestNightTemperature = 2 }, new AreaConfig(), minimum));
	}

	[Fact]
	public async Task With_no_frost_limit_the_setback_stops_at_the_room_thermostats_own_lowest_setting()
	{
		AreaSettings house = new() { LowestNightTemperature = 2, FrostProtection = false };
		AreaConfig room = new() { LowestNightTemperature = -3 };

		Assert.Equal(2.0, await SetBackToAsync(house, room, cardMinimum: 2.0));
	}

	[Theory]
	[InlineData(-20, true)]
	[InlineData(-21, false)]
	[InlineData(30, true)]
	[InlineData(31, false)]
	public void A_frost_temperature_is_held_to_minus_20_to_30(double frost, bool accepted)
	{
		HeatingConfig document = HeatingConfig.CreateDefault();
		document.Defaults.FrostTemperature = frost;

		ValidationResult result = ConfigValidator.Validate(document);

		Assert.Equal(accepted, !result.Errors.Any(error => error.Contains("FrostTemperature", StringComparison.Ordinal)));
	}

	[Theory]
	[InlineData(-20, true)]
	[InlineData(-20.5, false)]
	[InlineData(1.5, true)]
	[InlineData(30, true)]
	[InlineData(30.5, false)]
	public void A_lowest_night_temperature_is_held_to_minus_20_to_30(double lowest, bool accepted)
	{
		HeatingConfig document = HeatingConfig.CreateDefault();
		document.Defaults.LowestNightTemperature = lowest;

		ValidationResult result = ConfigValidator.Validate(document);

		Assert.Equal(accepted, !result.Errors.Any(error => error.Contains("LowestNightTemperature", StringComparison.Ordinal)));
	}

	[Fact]
	public void No_frost_limit_for_the_house_and_a_room_survives_a_save()
	{
		AHostedDocument document = AHostedDocument.Holding(
			$"""
			{HeatingConfigDocument.RootKey}:
			  ConfigName: Adaptive Heating
			  Areas:
			  - AreaId: kitchen
			""");

		HeatingConfig loaded = document.Host.Store.Load();
		loaded.Defaults.FrostProtection = false;
		loaded.Areas[0].FrostProtection = false;
		Assert.True(document.Host.Save(loaded).Written);

		HeatingConfig again = document.Host.Store.Load();

		Assert.False(again.Defaults.FrostProtection);
		Assert.False(again.Areas[0].FrostProtection);
	}

	private static async Task<double> SetBackToAsync(AreaSettings defaults, AreaConfig room, double? thermostatMinimum = null, double? cardMinimum = null)
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync(house: [TheRoomsFollowTheHouseModeTests.SelectorOn("Sover", Asleep)]);

		room.AreaId = ACabin.RoomId;
		room.NightSetback = true;

		DrivesTheRooms driver = ADrivenCabin.Over(
			open,
			new InMemorySettingsStore(Night()),
			new AReporter().Reports,
			outdoor: OutdoorChoice.Nothing,
			houseMode: TheRoomsFollowTheHouseModeTests.Cabin(),
			startsAt: Asleep,
			defaults: defaults,
			rooms: room).Driver;

		await driver.RunAsync(States(Thermostat(thermostatMinimum, cardMinimum)), Asleep.AddMinutes(5), CancellationToken.None);

		using JsonDocument sent = JsonDocument.Parse(Assert.Single(open.Core.Calls).Json);

		return Temperature(sent.RootElement) ?? throw new InvalidOperationException("No temperature was sent.");
	}

	private static double? Temperature(JsonElement element)
	{
		if (element.ValueKind is JsonValueKind.Object)
		{
			foreach (JsonProperty property in element.EnumerateObject())
			{
				if (property.Name == "temperature" && property.Value.ValueKind is JsonValueKind.Number)
					return property.Value.GetDouble();

				if (Temperature(property.Value) is { } inner)
					return inner;
			}
		}

		return null;
	}

	// 4 °C an hour measured at -20 °C outside, 30 W lost per degree, a 1500 W heater, 12 hours to the morning.
	private static HeatingDocument Night()
	{
		HeatingDocument document = ACabin.WithOneRoom(away: 9, home: 19);
		RoomSlice room = document.Rooms[0];

		Dictionary<HeatingMode, Stamped<double>> temperatures = new(room.ModeTemperatures)
		{
			[HeatingMode.Night] = ACabin.Held(19, Asleep),
		};

		return document with
		{
			NightSetbackInForce = true,
			NextPeriodStartsAt = Asleep.AddHours(12),
			Rooms =
			[
				room with
				{
					ModeTemperatures = temperatures,
					NextPeriodTemperature = 19,
					RateMeasurements = [new WarmingRateMeasurement(-20, 4.0, Asleep)],
					HeatLossMeasurements = [new HeatLossMeasurement(30, 1500, Asleep)]
				}
			]
		};
	}

	private static string Number(double? value) =>
		value is { } known ? known.ToString(CultureInfo.InvariantCulture) : "null";

	private static List<EntityState> States(params string[] entities) =>
		JsonSerializer.Deserialize<List<EntityState>>($"[{string.Join(",", entities)}]")!;

	private static string Thermostat(double? minimum, double? cardMinimum) =>
		$$"""
		{
			"entity_id": "{{ACabin.Thermostat}}",
			"state": "heat",
			"attributes": {
				"friendly_name": "Kitchen",
				"room_id": "{{ACabin.RoomId}}", "area_id": "{{ACabin.RoomId}}",
				"integration_version": "0.1.0",
				"vocabulary_version": 1,
				"temperature": 19,
				"current_temperature": 19,
				"hvac_action": "idle",
				"no_reading_at_all": false,
				"outdoor": -20,
				"power": 0,
				"has_a_power_meter": true,
				"reports_a_problem": false,
				"thermostat_minimum": {{Number(minimum)}},
				"min_temp": {{Number(cardMinimum)}},
				"target_set_at": "{{Asleep.ToString("O", CultureInfo.InvariantCulture)}}"
			},
			"context": { "id": "01JCARD", "parent_id": null, "user_id": null }
		}
		""";
}
