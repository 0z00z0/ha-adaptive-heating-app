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

/// <summary>A metered room under a sleep option marked for night setback.</summary>
public sealed class ARoomIsSetBackAtNightTests
{
	private static readonly DateTimeOffset Asleep = new(2026, 10, 20, 23, 0, 0, TimeSpan.Zero);
	private static readonly DateTimeOffset Morning = new(2026, 10, 21, 6, 30, 0, TimeSpan.Zero);

	[Fact]
	public async Task The_room_is_given_its_setback_in_place_of_the_night_temperature_and_a_lost_reading_ends_it()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync(house: [TheRoomsFollowTheHouseModeTests.SelectorOn("Sover", Asleep)]);
		DrivesTheRooms driver = Driver(open);

		await driver.RunAsync(States(Thermostat(target: 19.0, reading: 19.0)), Asleep.AddMinutes(5), CancellationToken.None);

		SentCall setBack = Assert.Single(open.Core.Calls);
		Assert.Contains("\"temperature\":15", setBack.Json, StringComparison.Ordinal);

		await driver.RunAsync(States(Thermostat(target: 15.0, reading: null)), Asleep.AddMinutes(6), CancellationToken.None);

		Assert.Equal(2, open.Core.Calls.Count);
		Assert.Contains("\"temperature\":19", open.Core.Calls[1].Json, StringComparison.Ordinal);
	}

	[Fact]
	public async Task A_room_whose_integration_reports_a_fault_keeps_its_night_temperature()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync(house: [TheRoomsFollowTheHouseModeTests.SelectorOn("Sover", Asleep)]);

		await Driver(open).RunAsync(States(Thermostat(target: 19.0, reading: 19.0, fault: true)), Asleep.AddMinutes(5), CancellationToken.None);

		Assert.Empty(open.Core.Calls);
	}

	[Fact]
	public async Task A_hand_change_made_during_the_night_still_wins()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync(house: [TheRoomsFollowTheHouseModeTests.SelectorOn("Sover", Asleep)]);

		await Driver(open).RunAsync(States(Thermostat(target: 22.0, reading: 19.0, setAt: Asleep.AddMinutes(2))), Asleep.AddMinutes(5), CancellationToken.None);

		Assert.Empty(open.Core.Calls);
	}

	[Fact]
	public async Task The_restart_is_a_warm_up_to_the_next_periods_temperature_by_its_start()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync(house: [TheRoomsFollowTheHouseModeTests.SelectorOn("Sover", Asleep)]);
		DrivesTheRooms driver = Driver(open);

		// Set back at 16 and holding it; the warm-up to 19 at 1.6 °C an hour takes 1 h 52 min 30 s.
		await driver.RunAsync(States(Thermostat(target: 16.0, reading: 16.0)), Morning.AddHours(-2), CancellationToken.None);
		Assert.DoesNotContain(open.Core.Calls, call => call.Action.EndsWith(HeatingActions.WarmRoomBy, StringComparison.Ordinal));

		await driver.RunAsync(States(Thermostat(target: 16.0, reading: 16.0)), Morning.AddMinutes(-112.5), CancellationToken.None);

		SentCall restart = Assert.Single(open.Core.Calls, call => call.Action.EndsWith(HeatingActions.WarmRoomBy, StringComparison.Ordinal));
		Assert.Contains("\"temperature\":19", restart.Json, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("Asleep", true)]
	[InlineData("Napping", false)]
	public void Only_the_marked_sleep_option_sets_back_and_the_next_period_names_the_restarts_goal(string option, bool marked)
	{
		HeatingConfig document = HeatingConfig.CreateDefault();
		document.Periods =
		[
			new TimePeriodConfig { Id = "period_001", Name = "Night", Start = "23:00", Temperature = 18 },
			new TimePeriodConfig { Id = "period_002", Name = "Morning", Start = "06:30", Temperature = 21 }
		];
		document.Areas = [new AreaConfig { AreaId = ACabin.RoomId }];
		document.Global.HouseMode = new HouseModeConfig
		{
			Entity = "input_select.house_mode",
			Options =
			[
				new HouseModeOptionConfig { Id = "Asleep", Value = "Asleep", Kind = ModeKind.Sleep, NightSetback = true },
				new HouseModeOptionConfig { Id = "Napping", Value = "Napping", Kind = ModeKind.Sleep }
			]
		};

		PeriodSpan? next = RoomTemperatures.Next(document, new SunTimes(null, null), Asleep.AddMinutes(5), TimeZoneInfo.Utc);
		HeatingDocument composed = RoomTemperatures.Compose(ACabin.WithOneRoom(), document, document.Periods[0], since: null, optionInForce: option, next);

		Assert.Equal(marked, composed.NightSetbackInForce);
		Assert.Equal(Morning, composed.NextPeriodStartsAt);
		Assert.Equal(21, composed.Rooms[0].NextPeriodTemperature);
	}

	private static DrivesTheRooms Driver(AnOpenConnection open) =>
		ADrivenCabin.Over(
			open,
			new InMemorySettingsStore(Night()),
			new AReporter().Reports,
			outdoor: OutdoorChoice.Nothing,
			houseMode: TheRoomsFollowTheHouseModeTests.Cabin(),
			startsAt: Asleep,
			rooms: new AreaConfig { AreaId = ACabin.RoomId, NightSetback = true }).Driver;

	// A fast room: 2 °C an hour, 30 W lost per degree, a 1500 W heater.
	private static HeatingDocument Night()
	{
		HeatingDocument document = ACabin.WithOneRoom(away: 10, home: 19);
		RoomSlice room = document.Rooms[0];

		Dictionary<HeatingMode, Stamped<double>> temperatures = new(room.ModeTemperatures)
		{
			[HeatingMode.Night] = ACabin.Held(19, Asleep),
		};

		return document with
		{
			NightSetbackInForce = true,
			NextPeriodStartsAt = Morning,
			Rooms =
			[
				room with
				{
					ModeTemperatures = temperatures,
					NextPeriodTemperature = 19,
					RateMeasurements = [new WarmingRateMeasurement(-5, 2.0, Asleep)],
					HeatLossMeasurements = [new HeatLossMeasurement(30, 1500, Asleep)]
				}
			]
		};
	}

	private static List<EntityState> States(params string[] entities) =>
		JsonSerializer.Deserialize<List<EntityState>>($"[{string.Join(",", entities)}]")!;

	private static string Thermostat(double target, double? reading, bool fault = false, DateTimeOffset? setAt = null) =>
		$$"""
		{
			"entity_id": "{{ACabin.Thermostat}}",
			"state": "heat",
			"attributes": {
				"friendly_name": "Kitchen",
				"room_id": "{{ACabin.RoomId}}", "area_id": "{{ACabin.RoomId}}",
				"integration_version": "0.1.0",
				"vocabulary_version": 1,
				"temperature": {{target.ToString(CultureInfo.InvariantCulture)}},
				"current_temperature": {{(reading is { } value ? value.ToString(CultureInfo.InvariantCulture) : "null")}},
				"hvac_action": "idle",
				"no_reading_at_all": false,
				"outdoor": -5,
				"power": 0,
				"has_a_power_meter": true,
				"reports_a_problem": {{(fault ? "true" : "false")}},
				"target_set_at": "{{(setAt ?? Asleep).ToString("O", CultureInfo.InvariantCulture)}}"
			},
			"context": { "id": "01JCARD", "parent_id": null, "user_id": null }
		}
		""";
}
