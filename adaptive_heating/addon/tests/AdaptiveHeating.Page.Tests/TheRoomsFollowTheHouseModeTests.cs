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

/// <summary>Every room's mode comes from the house-mode selector, as lighting's rules read it.</summary>
public sealed class TheRoomsFollowTheHouseModeTests
{
	private const string Selector = "input_select.husmodus";

	private static readonly DateTimeOffset Early = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
	private static readonly DateTimeOffset Later = Early.AddHours(1);
	private static readonly DateTimeOffset Started = Early.AddHours(2);

	[Fact]
	public async Task A_selector_moved_while_the_add_on_was_down_beats_an_earlier_hand_change_and_not_a_later_one()
	{
		// Away holds 4. The card was set to 15 at 09:00, the selector went to Borte at 10:00, the add-on starts at 11:00.
		await using AnOpenConnection moved = await AnOpenConnection.StartAsync(house: [SelectorOn("Borte", Later)]);

		await Driver(moved).RunAsync(States(Thermostat(15.0, Early)), Started, CancellationToken.None);

		SentCall called = Assert.Single(moved.Core.Calls);
		Assert.Contains("\"temperature\":4", called.Json, StringComparison.Ordinal);

		// The other order: the card moved after the selector, so the hand change stands.
		await using AnOpenConnection handLast = await AnOpenConnection.StartAsync(house: [SelectorOn("Borte", Early)]);

		await Driver(handLast).RunAsync(States(Thermostat(15.0, Later)), Started, CancellationToken.None);

		Assert.Empty(handLast.Core.Calls);
	}

	[Theory]
	[InlineData("Sover", ModeKind.Sleep, 16)]
	[InlineData("Borte", ModeKind.Away, 4)]
	[InlineData("Normal", ModeKind.Normal, 20)]
	public async Task The_cabins_selector_puts_the_mode_in_force(string option, ModeKind kind, double temperature)
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync(house: [SelectorOn(option, Later)]);

		DrivingPass pass = await Driver(open).RunAsync(States(Thermostat(10.0, Early)), Started, CancellationToken.None);

		Assert.Equal(kind, pass.House.ActiveKind);
		Assert.Equal(option, pass.House.ActiveModeName);

		SentCall called = Assert.Single(open.Core.Calls);
		Assert.Contains($"\"temperature\":{temperature.ToString(CultureInfo.InvariantCulture)}", called.Json, StringComparison.Ordinal);
	}

	internal static HouseModeConfig Cabin() =>
		new()
		{
			Entity = Selector,
			Options =
			[
				new HouseModeOptionConfig { Value = "Normal", Kind = ModeKind.Normal },
				new HouseModeOptionConfig { Value = "Borte", Kind = ModeKind.Away },
				new HouseModeOptionConfig { Value = "Sover", Kind = ModeKind.Sleep },
			]
		};

	private static DrivesTheRooms Driver(AnOpenConnection open) =>
		ADrivenCabin.Over(
			open,
			new InMemorySettingsStore(WithANightTemperature()),
			new AReporter().Reports,
			outdoor: OutdoorChoice.Nothing,
			houseMode: Cabin(),
			startsAt: Started).Driver;

	private static HeatingDocument WithANightTemperature()
	{
		HeatingDocument document = ACabin.WithOneRoom(away: 4, home: 20);

		Dictionary<HeatingMode, Stamped<double>> temperatures = new(document.Rooms[0].ModeTemperatures)
		{
			[HeatingMode.Night] = ACabin.Held(16),
		};

		return document with { Rooms = [document.Rooms[0] with { ModeTemperatures = temperatures }] };
	}

	internal static EntityState SelectorOn(string option, DateTimeOffset changed) =>
		JsonSerializer.Deserialize<EntityState>(
			$$"""
			{
				"entity_id": "{{Selector}}",
				"state": "{{option}}",
				"attributes": { "friendly_name": "Husmodus", "options": ["Normal", "Borte", "Sover"] },
				"last_changed": "{{changed.ToString("O", CultureInfo.InvariantCulture)}}"
			}
			""")!;

	private static List<EntityState> States(params string[] entities) =>
		JsonSerializer.Deserialize<List<EntityState>>($"[{string.Join(",", entities)}]")!;

	private static string Thermostat(double target, DateTimeOffset setAt) =>
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
				"current_temperature": 19.0,
				"hvac_action": "idle",
				"no_reading_at_all": false,
				"outdoor": -5,
				"target_set_at": "{{setAt.ToString("O", CultureInfo.InvariantCulture)}}"
			},
			"context": { "id": "01JCARD", "parent_id": null, "user_id": null }
		}
		""";
}
