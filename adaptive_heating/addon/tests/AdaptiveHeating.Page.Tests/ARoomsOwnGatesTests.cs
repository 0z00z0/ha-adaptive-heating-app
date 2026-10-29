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

/// <summary>A room's own switches on the room page reach the pass over the rooms, or the page edits nothing.</summary>
public sealed class ARoomsOwnGatesTests
{
	private const string Fireplace = "input_boolean.fireplace";

	// After the room's temperatures were typed, so a hand change can be newer than them.
	private static readonly DateTimeOffset Morning = ACabin.Typed.AddHours(1);

	[Fact]
	public async Task A_disabled_room_is_read_and_never_commanded()
	{
		AreaConfig room = new() { AreaId = ACabin.RoomId, Enabled = false };
		(AnOpenConnection open, InMemorySettingsStore store) = await Cabin("Borte");
		await using AnOpenConnection _ = open;

		await Driver(open, store, room)
			.RunAsync(States(Thermostat(20.0)), Morning, CancellationToken.None);

		Assert.Empty(open.Core.Calls);
		Assert.False(store.Read().VerdictFor(ACabin.RoomId)?.Enabled);
	}

	[Fact]
	public async Task A_room_paused_while_the_fireplace_burns_is_heated_as_in_away()
	{
		AreaConfig room = new() { AreaId = ACabin.RoomId, PauseWhenOn = [Fireplace] };
		(AnOpenConnection open, InMemorySettingsStore store) = await Cabin("Normal");
		await using AnOpenConnection _ = open;

		await Driver(open, store, room).RunAsync(States(Thermostat(20.0), Switch(Fireplace, "on")), Morning, CancellationToken.None);

		SentCall called = Assert.Single(open.Core.Calls);
		Assert.Contains("\"temperature\":4", called.Json, StringComparison.Ordinal);
		Assert.Equal(Fireplace, store.Read().VerdictFor(ACabin.RoomId)?.PausedBy);
	}

	[Fact]
	public async Task A_held_room_keeps_its_temperature_when_the_house_goes_away()
	{
		AreaConfig room = new() { AreaId = ACabin.RoomId, HoldWhenOn = [Fireplace] };
		(AnOpenConnection open, InMemorySettingsStore store) = await Cabin("Borte");
		await using AnOpenConnection _ = open;

		await Driver(open, store, room).RunAsync(States(Thermostat(20.0), Switch(Fireplace, "on")), Morning, CancellationToken.None);

		Assert.Empty(open.Core.Calls);
	}

	[Fact]
	public async Task A_guest_mode_holds_every_room_as_it_stands()
	{
		HouseModeConfig modes = TheRoomsFollowTheHouseModeTests.Cabin();
		modes.Options.Add(new HouseModeOptionConfig { Value = "Gjester", Kind = ModeKind.Guest });
		(AnOpenConnection open, InMemorySettingsStore store) = await Cabin("Gjester");
		await using AnOpenConnection _ = open;

		DrivesTheRooms driver = ADrivenCabin.Over(
			open, store, new AReporter().Reports, houseMode: modes, startsAt: Morning, rooms: new AreaConfig { AreaId = ACabin.RoomId }).Driver;

		await driver.RunAsync(States(Thermostat(17.0)), Morning, CancellationToken.None);

		Assert.Empty(open.Core.Calls);
		Assert.Equal(DrivesTheRooms.GuestHold, store.Read().VerdictFor(ACabin.RoomId)?.HeldBy);
	}

	[Fact]
	public async Task A_room_that_says_automations_are_no_hand_is_put_back_whatever_the_house_says()
	{
		AreaConfig room = new() { AreaId = ACabin.RoomId, TreatAutomationsAsManual = false };
		(AnOpenConnection open, InMemorySettingsStore store) = await Cabin("Borte");
		await using AnOpenConnection _ = open;

		DrivesTheRooms driver = Driver(open, store, room);

		await driver.RunAsync(States(Thermostat(20.0)), Morning, CancellationToken.None);
		await driver.RunAsync(
			States(Thermostat(21.0, Morning.AddSeconds(30), "01JAUTOMATION")),
			Morning.AddMinutes(1),
			CancellationToken.None);

		Assert.Equal(2, open.Core.Calls.Count);
	}

	[Fact]
	public async Task A_change_at_the_thermostat_holds_for_the_rooms_hold_length_and_no_longer()
	{
		AreaConfig room = new() { AreaId = ACabin.RoomId, OverrideDurationMinutes = 30 };
		(AnOpenConnection open, InMemorySettingsStore store) = await Cabin("Borte");
		await using AnOpenConnection _ = open;

		DrivesTheRooms driver = Driver(open, store, room);

		await driver.RunAsync(States(Thermostat(20.0)), Morning, CancellationToken.None);
		await driver.RunAsync(States(Thermostat(21.0, Morning.AddSeconds(30))), Morning.AddMinutes(1), CancellationToken.None);

		Assert.Single(open.Core.Calls);

		await driver.RunAsync(States(Thermostat(21.0, Morning.AddSeconds(30))), Morning.AddMinutes(40), CancellationToken.None);

		Assert.Equal(2, open.Core.Calls.Count);
	}

	[Fact]
	public async Task A_mode_switched_on_from_the_room_page_switches_a_room_with_no_reading()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync(house: [TheRoomsFollowTheHouseModeTests.SelectorOn("Normal", Morning)]);
		HeatingDocument cabin = ACabin.WithOneRoom();
		InMemorySettingsStore store = new(cabin with
		{
			Rooms = [cabin.Rooms[0] with { NoReadingAtAll = true, ModeSwitches = RoomModeSwitches.Starting(Stamp.Certain(default)) }]
		});

		Assert.True(store.SaveModeSwitch(ACabin.RoomId, HeatingMode.Home, on: true, Stamp.Certain(Morning)).Written);

		await Driver(open, store, new AreaConfig { AreaId = ACabin.RoomId })
			.RunAsync(States(Thermostat(4.0)), Morning, CancellationToken.None);

		SentCall called = Assert.Single(open.Core.Calls);
		Assert.Contains("\"switched\":\"on\"", called.Json, StringComparison.Ordinal);
	}

	private static async Task<(AnOpenConnection Open, InMemorySettingsStore Store)> Cabin(string option)
	{
		AnOpenConnection open = await AnOpenConnection.StartAsync(house: [TheRoomsFollowTheHouseModeTests.SelectorOn(option, Morning)]);

		return (open, new InMemorySettingsStore(ACabin.WithOneRoom(away: 4, home: 20)));
	}

	private static DrivesTheRooms Driver(AnOpenConnection open, ISettingsStore store, AreaConfig room) =>
		ADrivenCabin.Over(
			open,
			store,
			new AReporter().Reports,
			outdoor: OutdoorChoice.Nothing,
			houseMode: TheRoomsFollowTheHouseModeTests.Cabin(),
			startsAt: Morning,
			global: global => global.TreatAutomationsAsManual = true,
			rooms: room).Driver;

	private static List<EntityState> States(params string[] entities) =>
		JsonSerializer.Deserialize<List<EntityState>>($"[{string.Join(",", entities)}]")!;

	private static string Switch(string entityId, string state) =>
		$$"""{ "entity_id": "{{entityId}}", "state": "{{state}}", "attributes": {} }""";

	private static string Thermostat(double target, DateTimeOffset? setAt = null, string? parentContext = null) =>
		$$"""
		{
			"entity_id": "{{ACabin.Thermostat}}",
			"state": "heat",
			"attributes": {
				"room_id": "{{ACabin.RoomId}}", "area_id": "{{ACabin.RoomId}}",
				"integration_version": "0.1.0",
				"temperature": {{target.ToString(CultureInfo.InvariantCulture)}},
				"current_temperature": 19.0,
				"hvac_action": "idle",
				"target_set_at": "{{(setAt ?? new DateTimeOffset(2026, 9, 23, 8, 0, 0, TimeSpan.Zero)).ToString("O", CultureInfo.InvariantCulture)}}"
			},
			"context": { "id": "01JCHANGE", "parent_id": {{(parentContext is null ? "null" : $"\"{parentContext}\"")}}, "user_id": null }
		}
		""";
}
