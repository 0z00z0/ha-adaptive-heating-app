using System.Text.Json;

using Adaptive.Settings;

using AdaptiveHeating.AddOn.Integration;
using AdaptiveHeating.AddOn.Reporting;
using AdaptiveHeating.Page.Settings;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>A room whose thermostat Home Assistant placed in another area moves its entry there, with no call back.</summary>
public sealed class ARoomMovedInHomeAssistantTests
{
	private const string Pantry = "pantry";
	private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public async Task A_card_naming_another_area_moves_the_entry_and_says_so_with_no_call()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync();
		AHouse house = AHouse.Over(open, ACabin.RoomId);

		int moved = await house.Follower.FollowAsync([Card(ACabin.RoomId, Pantry)], Now, CancellationToken.None);

		Assert.Equal(1, moved);
		Assert.DoesNotContain(open.Core.Calls, call => call.Action.EndsWith(HeatingActions.MoveToArea, StringComparison.Ordinal));
		Assert.Equal(Pantry, Assert.Single(house.Document.Host.Store.Load().Areas).AreaId);
		HeatingReport report = Assert.Single(house.Reporter.Record.Entries, entry => entry.Report.What == WhatHappened.RoomMoved).Report;
		Assert.Equal(ACabin.RoomId, report.RoomId);
	}

	[Fact]
	public async Task An_area_the_settings_already_hold_keeps_the_room_where_it_is()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync();
		AHouse house = AHouse.Over(open, ACabin.RoomId, Pantry);

		int moved = await house.Follower.FollowAsync([Card(ACabin.RoomId, Pantry)], Now, CancellationToken.None);

		Assert.Equal(0, moved);
		Assert.Equal([ACabin.RoomId, Pantry], house.Document.Host.Store.Load().Areas.Select(area => area.AreaId));
	}

	[Fact]
	public async Task A_card_naming_its_own_area_moves_nothing()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync();
		AHouse house = AHouse.Over(open, ACabin.RoomId);

		int moved = await house.Follower.FollowAsync([Card(ACabin.RoomId, ACabin.RoomId)], Now, CancellationToken.None);

		Assert.Equal(0, moved);
		Assert.DoesNotContain(house.Reporter.Record.Entries, entry => entry.Report.What == WhatHappened.RoomMoved);
	}

	private static EntityState Card(string roomId, string area) =>
		JsonSerializer.Deserialize<EntityState>(
			$$"""
			{
				"entity_id": "{{ACabin.Thermostat}}",
				"state": "heat",
				"attributes": { "room_id": "{{roomId}}", "area_id": "{{area}}", "integration_version": "0.1.0", "vocabulary_version": 2 },
				"context": { "id": "01JCARD", "parent_id": null, "user_id": null }
			}
			""")!;

	private sealed record AHouse(
		RoomsMovedInHomeAssistant Follower,
		TellsTheThermostats Store,
		AHostedDocument Document,
		AReporter Reporter)
	{
		public static AHouse Over(AnOpenConnection open, params string[] areas)
		{
			HeatingConfig config = HeatingConfig.CreateDefault();
			config.Areas = [.. areas.Select(area => new AreaConfig { AreaId = area })];
			AHostedDocument document = AHostedDocument.Holding(HeatingConfigDocument.Serialize(config));
			document.Host.Reload();

			TellsTheThermostats store = new(
				new InMemorySettingsStore(ACabin.WithOneRoom()), open.Thermostats, NullLogger<TellsTheThermostats>.Instance);
			AReporter reporter = new();

			return new AHouse(
				new RoomsMovedInHomeAssistant(store, document.Host, reporter.Reports, NullLogger<RoomsMovedInHomeAssistant>.Instance),
				store,
				document,
				reporter);
		}
	}
}
