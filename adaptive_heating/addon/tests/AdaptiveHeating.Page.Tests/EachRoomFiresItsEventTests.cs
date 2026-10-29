using System.Globalization;
using System.Text.Json;

using Adaptive.Settings;

using AdaptiveHeating.AddOn.Driving;
using AdaptiveHeating.AddOn.Settings;
using AdaptiveHeating.Page.Settings;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Ha;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>Lighting's room event: automations follow it, so it fires on news and stays quiet on a steady pass.</summary>
public sealed class EachRoomFiresItsEventTests
{
	private static readonly DateTimeOffset Morning = ACabin.Typed.AddHours(1);

	[Fact]
	public async Task A_steady_room_fires_once_and_a_change_at_the_thermostat_fires_again()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync(house: [TheRoomsFollowTheHouseModeTests.SelectorOn("Normal", Morning)]);
		Published events = new();
		DrivesTheRooms driver = ADrivenCabin.Over(
			open,
			new InMemorySettingsStore(ACabin.WithOneRoom(away: 4, home: 20)),
			new AReporter().Reports,
			outdoor: OutdoorChoice.Nothing,
			houseMode: TheRoomsFollowTheHouseModeTests.Cabin(),
			startsAt: Morning,
			publisher: events,
			rooms: new AreaConfig { AreaId = ACabin.RoomId }).Driver;

		await driver.RunAsync(States(Thermostat(20.0)), Morning, CancellationToken.None);
		await driver.RunAsync(States(Thermostat(20.0)), Morning.AddMinutes(1), CancellationToken.None);
		await driver.RunAsync(States(Thermostat(22.0, Morning.AddMinutes(2))), Morning.AddMinutes(2), CancellationToken.None);

		Assert.Equal(
			[(TransitionReason.Startup, AreaState.AutoActive), (TransitionReason.ManualOn, AreaState.OverriddenOn)],
			events.Seen.Select(snapshot => (snapshot.Reason, snapshot.State)));
		Assert.Equal("adaptive_heating_area", HaStatePublisher.EventType);
	}

	[Fact]
	public async Task The_event_carries_the_cards_count_of_heaters_and_of_those_not_answering()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync(house: [TheRoomsFollowTheHouseModeTests.SelectorOn("Normal", Morning)]);
		Published events = new();
		DrivesTheRooms driver = ADrivenCabin.Over(
			open,
			new InMemorySettingsStore(ACabin.WithOneRoom(away: 4, home: 20)),
			new AReporter().Reports,
			outdoor: OutdoorChoice.Nothing,
			houseMode: TheRoomsFollowTheHouseModeTests.Cabin(),
			startsAt: Morning,
			publisher: events,
			rooms: new AreaConfig { AreaId = ACabin.RoomId }).Driver;

		await driver.RunAsync(States(Thermostat(20.0, heaters: (3, 1))), Morning, CancellationToken.None);
		await driver.RunAsync(States(Thermostat(20.0, heaters: (3, 0))), Morning.AddMinutes(1), CancellationToken.None);

		Assert.Equal([(1, 3), (0, 3)], events.Seen.Select(snapshot => (snapshot.HeatersNotResponding, snapshot.HeaterCount)));
	}

	private sealed class Published : IStatePublisher
	{
		public List<AreaSnapshot> Seen { get; } = [];

		public void Publish(AreaSnapshot snapshot) => Seen.Add(snapshot);
	}

	private static List<EntityState> States(params string[] entities) =>
		JsonSerializer.Deserialize<List<EntityState>>($"[{string.Join(",", entities)}]")!;

	private static string Thermostat(double target, DateTimeOffset? setAt = null, (int Count, int NotAnswering)? heaters = null) =>
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
				"target_set_at": "{{(setAt ?? new DateTimeOffset(2026, 9, 23, 8, 0, 0, TimeSpan.Zero)).ToString("O", CultureInfo.InvariantCulture)}}"{{Counted(heaters)}}
			},
			"context": { "id": "01JCHANGE", "parent_id": null, "user_id": null }
		}
		""";

	private static string Counted((int Count, int NotAnswering)? heaters) =>
		heaters is { } counted ? $", \"heater_count\": {counted.Count}, \"heaters_not_responding\": {counted.NotAnswering}" : "";
}
