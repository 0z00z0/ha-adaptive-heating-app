using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using Adaptive.Settings;

using AdaptiveHeating.AddOn.Driving;
using AdaptiveHeating.AddOn.Integration;
using AdaptiveHeating.AddOn.Settings;
using AdaptiveHeating.Page.Settings;
using AdaptiveHeating.Planner;

using AdaptiveLighting.Abstractions;

using Microsoft.Extensions.Logging;

using Xunit;

using ChangeOrigin = AdaptiveHeating.Planner.ChangeOrigin;

namespace AdaptiveHeating.Page.Tests;

/// <summary>A room's mode is the preset on its own card; the add-on writes one only when a period begins or the house mode moves.</summary>
public sealed class ARoomRunsThePresetOnItsCardTests
{
	private const string Kitchen = ACabin.Thermostat;
	private const string Stue = "climate.stue";

	// Home Assistant's TARGET_TEMPERATURE and PRESET_MODE flags; the integration publishes both.
	private const int WithPresets = 17;
	private const int WithoutPresets = 1;

	private static readonly DateTimeOffset ModeChosen = ACabin.Typed;
	private static readonly DateTimeOffset Start = ACabin.Typed.AddHours(2);

	[Fact]
	public async Task A_preset_chosen_on_one_card_runs_that_room_in_its_mode_and_no_other()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync(house: [TheRoomsFollowTheHouseModeTests.SelectorOn("Normal", ModeChosen)]);
		DrivesTheRooms driver = Cabin(open, new InMemorySettingsStore(TwoRooms(home: 20))).Driver;

		// The kitchen was set by hand after the house went Normal and is still inside its hold; its card was then set to
		// away, which is the newer change. The living room carries no preset.
		await driver.RunAsync(
			States(
				Thermostat(Kitchen, ACabin.RoomId, 20, "away", Start.AddHours(-1), setAt: Start.AddMinutes(-90)),
				Thermostat(Stue, "stue", 20)),
			Start,
			CancellationToken.None);

		SentCall called = Assert.Single(open.Core.Calls);
		Assert.Equal("climate.set_temperature", called.Action);
		Assert.Equal(Kitchen, EntityOf(called));
		Assert.Contains("\"temperature\":4", called.Json, StringComparison.Ordinal);
	}

	[Fact]
	public async Task No_pass_writes_a_preset_until_a_period_begins_and_none_after_it()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync(house: [TheRoomsFollowTheHouseModeTests.SelectorOn("Normal", ModeChosen)]);
		InMemorySettingsStore store = new(TwoRooms(home: 20));
		DrivesTheRooms driver = Cabin(open, store).Driver;

		for (int minute = 0; minute < 4; minute++)
			await driver.RunAsync(BothOnTheirCards("away", 4), Start.AddMinutes(minute), CancellationToken.None);

		Assert.Empty(PresetsWritten(open));

		store.Receive(store.Read() with { PeriodKeyInForce = "evening" });

		for (int minute = 4; minute < 8; minute++)
			await driver.RunAsync(BothOnTheirCards("away", 4), Start.AddMinutes(minute), CancellationToken.None);

		Assert.Equal(2, PresetsWritten(open).Count);
	}

	[Fact]
	public async Task A_period_beginning_releases_every_card_and_tells_each_room_the_periods_temperature()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync(house: [TheRoomsFollowTheHouseModeTests.SelectorOn("Normal", ModeChosen)]);
		InMemorySettingsStore store = new(TwoRooms(home: 20));
		DrivesTheRooms driver = Cabin(open, store).Driver;

		await driver.RunAsync(BothOnTheirCards("away", 4), Start, CancellationToken.None);
		Assert.Empty(open.Core.Calls);

		// The evening period holds 18 in both rooms.
		store.Receive(TwoRooms(home: 18) with { PeriodKeyInForce = "evening" });
		await driver.RunAsync(BothOnTheirCards("away", 4), Start.AddMinutes(1), CancellationToken.None);

		Assert.Equal(
			[
				("climate.set_preset_mode", Kitchen, "\"preset_mode\":\"home\""),
				("climate.set_temperature", Kitchen, "\"temperature\":18"),
				("climate.set_preset_mode", Stue, "\"preset_mode\":\"home\""),
				("climate.set_temperature", Stue, "\"temperature\":18")
			],
			open.Core.Calls.Select(Described));
	}

	[Fact]
	public async Task A_house_mode_change_writes_its_preset_to_every_room()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync(house: [TheRoomsFollowTheHouseModeTests.SelectorOn("Normal", ModeChosen)]);
		DrivesTheRooms driver = Cabin(open, new InMemorySettingsStore(TwoRooms(home: 20))).Driver;

		await driver.RunAsync(BothOnTheirCards("home", 20), Start, CancellationToken.None);
		open.Core.Reports(TheRoomsFollowTheHouseModeTests.SelectorOn("Borte", Start.AddMinutes(1)));
		await driver.RunAsync(BothOnTheirCards("home", 20), Start.AddMinutes(2), CancellationToken.None);

		Assert.Equal(
			[$"{Kitchen} away", $"{Stue} away"],
			PresetsWritten(open));
	}

	[Fact]
	public async Task A_room_whose_thermostat_takes_no_preset_is_never_written_one()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync(house: [TheRoomsFollowTheHouseModeTests.SelectorOn("Normal", ModeChosen)]);
		InMemorySettingsStore store = new(TwoRooms(home: 20));
		DrivesTheRooms driver = Cabin(open, store).Driver;

		List<EntityState> states = States(
			Thermostat(Kitchen, ACabin.RoomId, 20, "home", ModeChosen),
			Thermostat(Stue, "stue", 20, features: WithoutPresets));

		await driver.RunAsync(states, Start, CancellationToken.None);
		store.Receive(store.Read() with { PeriodKeyInForce = "evening" });
		await driver.RunAsync(states, Start.AddMinutes(1), CancellationToken.None);

		Assert.Equal([$"{Kitchen} home"], PresetsWritten(open));
	}

	[Fact]
	public async Task A_preset_the_integration_does_not_know_is_warned_once_per_name_and_the_house_mode_applies()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync(house: [TheRoomsFollowTheHouseModeTests.SelectorOn("Normal", ModeChosen)]);
		LogUnderTest<DrivesTheRooms> log = new();
		DrivesTheRooms driver = Cabin(open, new InMemorySettingsStore(TwoRooms(home: 20)), log).Driver;

		foreach ((int minute, string preset) in new[] { (0, "holiday"), (1, "holiday"), (2, "holiday"), (3, "cottage") })
		{
			await driver.RunAsync(
				States(Thermostat(Kitchen, ACabin.RoomId, 20, preset, ModeChosen), Thermostat(Stue, "stue", 20)),
				Start.AddMinutes(minute),
				CancellationToken.None);
		}

		Assert.Equal(
			["holiday", "cottage"],
			log.Lines.Where(line => line.Level == LogLevel.Warning).Select(line => Regex.Match(line.Said, "preset '([^']+)'").Groups[1].Value));
		Assert.Empty(open.Core.Calls);
	}

	[Fact]
	public async Task A_save_that_moves_the_period_is_no_boundary()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync(house: [TheRoomsFollowTheHouseModeTests.SelectorOn("Normal", ModeChosen)]);
		InMemorySettingsStore store = new(TwoRooms(home: 20));
		ADrivenCabin cabin = Cabin(open, store);

		await cabin.Driver.RunAsync(BothOnTheirCards("home", 20), Start, CancellationToken.None);

		cabin.SavesTheDocument();
		store.Receive(store.Read() with { PeriodKeyInForce = "evening" });
		await cabin.Driver.RunAsync(BothOnTheirCards("home", 20), Start.AddMinutes(1), CancellationToken.None);

		Assert.Empty(PresetsWritten(open));
	}

	private static ADrivenCabin Cabin(
		AnOpenConnection open,
		InMemorySettingsStore store,
		ILogger<DrivesTheRooms>? logger = null,
		IStatePublisher? publisher = null) =>
		ADrivenCabin.Over(
			open,
			store,
			new AReporter().Reports,
			outdoor: OutdoorChoice.Nothing,
			houseMode: TheRoomsFollowTheHouseModeTests.Cabin(),
			startsAt: Start,
			publisher: publisher,
			logger: logger);

	private static HeatingDocument TwoRooms(double home)
	{
		HeatingDocument one = ACabin.WithOneRoom(away: 4, home: home);
		RoomSlice kitchen = one.Rooms[0];
		RoomSlice stue = kitchen with { Id = "stue", Name = "Living room", Thermostat = Stue };

		return one with { Rooms = [kitchen, stue], PeriodKeyInForce = "morning" };
	}

	private static List<EntityState> BothOnTheirCards(string preset, double target) =>
		States(
			Thermostat(Kitchen, ACabin.RoomId, target, preset, ModeChosen),
			Thermostat(Stue, "stue", target, preset, ModeChosen));

	private static List<string> PresetsWritten(AnOpenConnection open) =>
		[
			.. open.Core.Calls
				.Where(call => call.Action == "climate.set_preset_mode")
				.Select(call => $"{EntityOf(call)} {JsonDocument.Parse(call.Json).RootElement.GetProperty("service_data").GetProperty("preset_mode").GetString()}")
		];

	private static (string Action, string Entity, string Field) Described(SentCall call) =>
		(call.Action, EntityOf(call), call.Action == "climate.set_preset_mode"
			? $"\"preset_mode\":\"{JsonDocument.Parse(call.Json).RootElement.GetProperty("service_data").GetProperty("preset_mode").GetString()}\""
			: $"\"temperature\":{JsonDocument.Parse(call.Json).RootElement.GetProperty("service_data").GetProperty("temperature").GetDouble().ToString(CultureInfo.InvariantCulture)}");

	private static string EntityOf(SentCall call) =>
		Regex.Match(call.Json, @"""entity_id"":\[?""([^""]+)""").Groups[1].Value;

	private static List<EntityState> States(params string[] entities) =>
		JsonSerializer.Deserialize<List<EntityState>>($"[{string.Join(",", entities)}]")!;

	private static string Thermostat(
		string entity,
		string room,
		double target,
		string? preset = null,
		DateTimeOffset? presetSetAt = null,
		int features = WithPresets,
		DateTimeOffset? setAt = null) =>
		$$"""
		{
			"entity_id": "{{entity}}",
			"state": "heat",
			"attributes": {
				"room_id": "{{room}}", "area_id": "{{room}}",
				"integration_version": "0.1.0",
				"temperature": {{target.ToString(CultureInfo.InvariantCulture)}},
				"current_temperature": 19.0,
				"hvac_action": "idle",
				"target_set_at": "{{(setAt ?? ACabin.Typed.AddHours(-1)).ToString("O", CultureInfo.InvariantCulture)}}",
				"supported_features": {{features}},
				"preset_mode": {{(preset is null ? "null" : $"\"{preset}\"")}},
				"preset_set_at": {{(presetSetAt is { } at ? $"\"{at.ToString("O", CultureInfo.InvariantCulture)}\"" : "null")}}
			},
			"context": { "id": "01JCARD", "parent_id": null, "user_id": null }
		}
		""";

	private sealed class Published : IStatePublisher
	{
		public List<AreaSnapshot> Seen { get; } = [];

		public void Publish(AreaSnapshot snapshot) => Seen.Add(snapshot);
	}
}
