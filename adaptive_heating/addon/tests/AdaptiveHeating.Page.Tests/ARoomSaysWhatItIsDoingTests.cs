using System.Globalization;
using System.Text.Json;

using AdaptiveHeating.AddOn.Driving;
using AdaptiveHeating.AddOn.Integration;
using AdaptiveHeating.AddOn.Settings;
using AdaptiveHeating.Page.Settings;
using AdaptiveHeating.Planner;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>
/// The settings page showed what a room is set up to do and never what it is doing, so a person could not tell a
/// room that had stopped from one holding steady. What a room reads and what it is being told both have to reach
/// the page.
/// </summary>
public sealed class ARoomSaysWhatItIsDoingTests
{
	private static readonly DateTimeOffset Morning = new(2026, 9, 25, 7, 0, 0, TimeSpan.Zero);

	[Fact]
	public async Task What_a_room_reads_and_what_it_is_told_both_reach_the_page()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync(
			house: [TheRoomsFollowTheHouseModeTests.SelectorOn("Borte", Morning)]);

		InMemorySettingsStore store = new(ACabin.WithOneRoom(away: 4, home: 20));

		// Nothing before the pass: the page opens on the settings and knows nothing about the room's state.
		Assert.Null(store.Read().NowFor(ACabin.RoomId));
		Assert.Null(store.Read().ToldFor(ACabin.RoomId));

		DrivesTheRooms driver = ADrivenCabin.Over(
			open, store, new AReporter().Reports, houseMode: TheRoomsFollowTheHouseModeTests.Cabin(), startsAt: Morning).Driver;

		await driver.RunAsync(States(Thermostat(current: 18.6, target: 4.0)), Morning, CancellationToken.None);

		HeatingDocument document = store.Read();
		RoomNow reading = Assert.IsType<RoomNow>(document.NowFor(ACabin.RoomId));

		// What the thermostat reads, what it holds, whether the relay is closed, and when it was read.
		Assert.Equal(ACabin.RoomId, reading.RoomId);
		Assert.Equal(18.6, reading.Temperature);
		Assert.Equal(4.0, reading.Target);
		Assert.False(reading.HeatingNow);
		Assert.Equal(Morning, reading.ReadAt);

		// And what the room is being told, which is the away temperature the selector's Borte stands for.
		Stamped<double> told = Assert.IsType<Stamped<double>>(document.ToldFor(ACabin.RoomId));
		Assert.Equal(4.0, told.Value);
		Assert.Equal(nameof(HeatingMode.Away), document.InForce.Mode?.Value);

		// A reading is not a setting, so none of it reaches the settings file.
		string written = JsonSerializer.Serialize(document, StoredSettings.SerializerOptions);

		Assert.DoesNotContain("\"now\"", written, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("\"inForce\"", written, StringComparison.OrdinalIgnoreCase);
	}

	private static List<EntityState> States(params string[] entities) =>
		JsonSerializer.Deserialize<List<EntityState>>($"[{string.Join(",", entities)}]")!;

	private static string Thermostat(double current, double target) =>
		$$"""
		{
			"entity_id": "{{ACabin.Thermostat}}",
			"state": "heat",
			"attributes": {
				"friendly_name": "Kitchen",
				"room_id": "{{ACabin.RoomId}}", "area_id": "{{ACabin.RoomId}}",
				"integration_version": "0.1.0",
				"temperature": {{Invariant(target)}},
				"current_temperature": {{Invariant(current)}},
				"hvac_action": "idle",
				"target_set_at": "2026-09-24T08:00:00+00:00"
			}
		}
		""";

	// A temperature written the Norwegian way is not a number to a JSON reader.
	private static string Invariant(double value) => value.ToString(CultureInfo.InvariantCulture);
}
