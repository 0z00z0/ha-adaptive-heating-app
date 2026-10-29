using System.Globalization;
using System.Text.Json;

using Adaptive.Settings;

using AdaptiveHeating.AddOn.Driving;
using AdaptiveHeating.AddOn.Integration;
using AdaptiveHeating.AddOn.Settings;
using AdaptiveHeating.Page.Presentation;
using AdaptiveHeating.Page.Settings;
using AdaptiveHeating.Planner;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>
/// A mode has to reach a room as an instruction, or the settings page writes into itself and the heating never
/// follows anything a person set.
/// </summary>
public sealed class DrivingTheRoomsTests
{
	private static readonly DateTimeOffset Morning = new(2026, 9, 24, 7, 0, 0, TimeSpan.Zero);

	[Fact]
	public async Task A_mode_reaches_the_room_as_a_temperature()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync(
			house: [TheRoomsFollowTheHouseModeTests.SelectorOn("Borte", Morning)]);

		DrivesTheRooms driver = Driver(open, ACabin.WithOneRoom(away: 4, home: 20), TheRoomsFollowTheHouseModeTests.Cabin());

		DrivingPass pass = await driver.RunAsync(States(Thermostat(target: 20.0)), Morning, CancellationToken.None);

		Assert.Equal(HeatingMode.Away, pass.Mode);

		SentCall called = Assert.Single(open.Core.Calls);

		Assert.Equal($"{HeatingActions.ClimateDomain}.{HeatingActions.SetTemperature}", called.Action);
		Assert.Contains("\"temperature\":4", called.Json, StringComparison.Ordinal);
		Assert.Contains($"\"entity_id\":[\"{ACabin.Thermostat}\"]", called.Json, StringComparison.Ordinal);
	}

	[Fact]
	public async Task A_room_with_no_reading_is_switched_on_or_off_in_a_modes_place()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync(
			house: [TheRoomsFollowTheHouseModeTests.SelectorOn("Borte", Morning)]);

		DrivesTheRooms driver = Driver(open, WithoutAReading(), TheRoomsFollowTheHouseModeTests.Cabin());

		await driver.RunAsync(
			States(Thermostat(target: 4.0, current: null, noReading: true)),
			Morning,
			CancellationToken.None);

		// The away cell starts on, because the dial on the heater is the only frost protection such a room has.
		SentCall called = Assert.Single(open.Core.Calls);

		Assert.Equal($"{HeatingActions.Domain}.{HeatingActions.SetRegulation}", called.Action);
		Assert.Contains("\"switched\":\"on\"", called.Json, StringComparison.Ordinal);

		open.Core.Reports(TheRoomsFollowTheHouseModeTests.SelectorOn("Normal", Morning.AddMinutes(1)));

		await driver.RunAsync(
			States(Thermostat(target: 4.0, current: null, noReading: true)),
			Morning.AddMinutes(1),
			CancellationToken.None);

		Assert.Equal(2, open.Core.Calls.Count);
		Assert.Contains("\"switched\":\"off\"", open.Core.Calls[1].Json, StringComparison.Ordinal);

		// No temperature is ever sent to such a room: it has none, and its own dial holds one.
		Assert.DoesNotContain(open.Core.Calls, call =>
			call.Action.EndsWith(HeatingActions.SetTemperature, StringComparison.Ordinal));
	}

	private static HeatingDocument WithoutAReading()
	{
		HeatingDocument document = ACabin.WithOneRoom();

		return document with
		{
			Rooms =
			[
				document.Rooms[0] with
				{
					NoReadingAtAll = true,
					ModeSwitches = RoomModeSwitches.Starting(Stamp.Certain(default)),
				},
			],
		};
	}

	private static DrivesTheRooms Driver(AnOpenConnection open, HeatingDocument document, HouseModeConfig? houseMode = null) =>
		ADrivenCabin.Over(
			open,
			new InMemorySettingsStore(document),
			new AReporter().Reports,
			outdoor: OutdoorChoice.Nothing,
			houseMode: houseMode,
			startsAt: Morning).Driver;

	private static List<EntityState> States(params string[] entities) =>
		JsonSerializer.Deserialize<List<EntityState>>($"[{string.Join(",", entities)}]")!;

	private static string Thermostat(
		double target,
		double? current = 19.0,
		bool noReading = false,
		string setAt = "2026-09-23T08:00:00+00:00") =>
		$$"""
		{
			"entity_id": "{{ACabin.Thermostat}}",
			"state": "heat",
			"attributes": {
				"friendly_name": "Kitchen",
				"room_id": "{{ACabin.RoomId}}", "area_id": "{{ACabin.RoomId}}",
				"integration_version": "0.1.0",
				"vocabulary_version": 1,
				"temperature": {{Invariant(target)}},
				"current_temperature": {{(current is { } reading ? Invariant(reading) : "null")}},
				"hvac_action": "idle",
				"no_reading_at_all": {{(noReading ? "true" : "false")}},
				"outdoor": -5,
				"target_set_at": "{{setAt}}"
			}
		}
		""";

	// A temperature written the Norwegian way is not a number to a JSON reader.
	private static string Invariant(double value) => value.ToString(CultureInfo.InvariantCulture);
}
