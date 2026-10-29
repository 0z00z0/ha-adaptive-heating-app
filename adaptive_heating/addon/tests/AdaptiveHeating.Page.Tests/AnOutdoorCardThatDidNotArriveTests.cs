using System.Globalization;
using System.Text.Json;

using Adaptive.Settings;

using AdaptiveHeating.AddOn.Driving;
using AdaptiveHeating.AddOn.Integration;
using AdaptiveHeating.AddOn.Settings;
using AdaptiveHeating.Page.Settings;
using AdaptiveHeating.Planner;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>
/// Every chosen outdoor sensor failing is worth one card and not one a minute, so it is said once while it stays
/// true. Latching that on the attempt rather than on the card arriving loses the card altogether on a cabin whose
/// sensors were already out when the add-on started, and nobody is told.
/// </summary>
public sealed class AnOutdoorCardThatDidNotArriveTests
{
	private static readonly DateTimeOffset Morning = new(2026, 9, 25, 7, 0, 0, TimeSpan.Zero);

	[Fact]
	public async Task A_card_refused_for_want_of_a_connection_is_raised_again_and_one_that_arrived_is_not_repeated()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync();

		AReporter reporter = new();
		DrivesTheRooms driving = Driver(open, reporter);

		reporter.Cards.Refuses = true;

		await driving.RunAsync(EveryOutdoorSensorQuiet(), Morning, CancellationToken.None);

		ACard refused = Assert.Single(reporter.Cards.Raised);

		// The next pass tries again, because nothing reached Home Assistant and the record is no substitute.
		await driving.RunAsync(EveryOutdoorSensorQuiet(), Morning.AddMinutes(1), CancellationToken.None);

		Assert.Equal(2, reporter.Cards.Raised.Count);
		Assert.Equal(refused, reporter.Cards.Raised[1]);

		// The pass that gets through is the last one to raise it.
		reporter.Cards.Refuses = false;

		await driving.RunAsync(EveryOutdoorSensorQuiet(), Morning.AddMinutes(2), CancellationToken.None);

		Assert.Equal(3, reporter.Cards.Raised.Count);

		// The other direction, or the assertion above proves only that the card is raised every minute: two more
		// passes with the sensors still out say nothing, because the card a person can see is already up.
		await driving.RunAsync(EveryOutdoorSensorQuiet(), Morning.AddMinutes(3), CancellationToken.None);
		await driving.RunAsync(EveryOutdoorSensorQuiet(), Morning.AddMinutes(4), CancellationToken.None);

		Assert.Equal(3, reporter.Cards.Raised.Count);
	}

	[Fact]
	public async Task A_card_whose_condition_still_holds_is_raised_again_after_the_connection_comes_back()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync();

		AReporter reporter = new();
		DrivesTheRooms driving = Driver(open, reporter);

		await driving.RunAsync(EveryOutdoorSensorQuiet(), Morning, CancellationToken.None);

		ACard showing = Assert.Single(reporter.Cards.Raised);

		// Home Assistant holds no cards after a core restart, and this side cannot see what it is still showing.
		driving.TheConnectionCameBack();

		await driving.RunAsync(EveryOutdoorSensorQuiet(), Morning.AddMinutes(1), CancellationToken.None);

		Assert.Equal(2, reporter.Cards.Raised.Count);
		Assert.Equal(showing, reporter.Cards.Raised[1]);

		// The other direction, or the assertion above proves only that the card is raised every minute: one
		// reconnection is worth one re-raise, and the passes after it are silent again.
		await driving.RunAsync(EveryOutdoorSensorQuiet(), Morning.AddMinutes(2), CancellationToken.None);

		Assert.Equal(2, reporter.Cards.Raised.Count);
	}

	/// <summary>Both chosen sensors quiet past the house's stale-after limit, which is what two dead batteries look like, and a forecast above them.</summary>
	private static List<EntityState> EveryOutdoorSensorQuiet()
	{
		// An outdoor sensor is out only after the house's stale-after limit, never after a room sensor's freshness.
		DateTimeOffset longAgo = Morning - TimeSpan.FromMinutes(new GlobalConfig().OutdoorSensorStaleAfterMinutes) - TimeSpan.FromMinutes(5);

		return States(
			Sensor(ACabin.OutdoorSensor, -4.0, longAgo),
			Sensor(ACabin.SecondOutdoorSensor, -4.4, longAgo),
			Forecast(2.0));
	}

	private static DrivesTheRooms Driver(AnOpenConnection open, AReporter reporter) =>
		ADrivenCabin.Over(open, new InMemorySettingsStore(ACabin.WithOneRoom()), reporter.Reports, outdoor: ACabin.Outdoor).Driver;

	private static List<EntityState> States(params string[] entities) =>
		JsonSerializer.Deserialize<List<EntityState>>($"[{string.Join(",", entities)}]")!;

	private static string Sensor(string entityId, double temperature, DateTimeOffset reportedAt) =>
		$$"""
		{
			"entity_id": "{{entityId}}",
			"state": "{{Invariant(temperature)}}",
			"last_updated": "{{Moment(reportedAt)}}",
			"last_changed": "{{Moment(reportedAt)}}",
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

	// A temperature written the Norwegian way is not a number to a JSON reader, and a state is a machine value.
	private static string Invariant(double value) => value.ToString(CultureInfo.InvariantCulture);
}
