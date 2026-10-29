using System.Globalization;
using System.Reactive.Concurrency;
using System.Text.Json;

using Adaptive.Engine;
using Adaptive.Settings;

using AdaptiveHeating.AddOn.Driving;
using AdaptiveHeating.AddOn.Integration;
using AdaptiveHeating.AddOn.Reporting;
using AdaptiveHeating.AddOn.Settings;
using AdaptiveHeating.Page.Settings;
using AdaptiveHeating.Planner;

using Microsoft.Extensions.Logging.Abstractions;

using NetDaemon.Client.HomeAssistant.Model;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>
/// A pass reads whatever the last thing heard from Home Assistant left behind, so a pass running before the
/// connection opens reads an empty house and reports on an empty cabin: no presence, no outdoor sensor and no
/// forecast, none of which it can see yet.
/// </summary>
public sealed class APassBeforeTheConnectionOpensTests
{
	[Fact]
	public async Task No_pass_runs_until_the_connection_is_open_and_one_runs_as_soon_as_it_is()
	{
		await using AnOpenConnection starting = await AnOpenConnection.StartingAsync(
			answer: null,
			CoreUnderTest.Thermostat());

		AReporter reporter = new();
		InMemorySettingsStore store = new(ACabin.WithOneRoom());

		using ConnectionToTheIntegration passes = Passes(starting, store, reporter);

		await passes.StartAsync(CancellationToken.None);

		// The add-on asks for its first pass as it starts, and nothing is connected yet.
		Assert.False(
			await AnOpenConnection.UntilAsync(() => reporter.Record.Read().Entries.Count > 0),
			"A pass ran and reported on a house nothing had been read from.");

		starting.Runner.HandOver(starting.Core);
		await starting.TheModelComesUpAsync();

		// The other direction, or the assertion above proves only that no pass ever runs: the house being read
		// asks for a pass of its own, and it is the first one to report anything.
		Assert.True(
			await AnOpenConnection.UntilAsync(() => reporter.Record.Read().Entries.Count > 0),
			"No pass ran once the connection was open.");

		Assert.Contains(reporter.Record.Read().Entries, entry => entry.Report.What == WhatHappened.ModeInForce);

		await passes.StopAsync(CancellationToken.None);
	}

	[Fact]
	public async Task A_connection_that_came_back_between_two_passes_raises_the_standing_card_again()
	{
		await using AnOpenConnection starting = await AnOpenConnection.StartingAsync(
			answer: null,
			QuietSensor(ACabin.OutdoorSensor, -4.0),
			QuietSensor(ACabin.SecondOutdoorSensor, -4.4));

		AReporter reporter = new();

		using ConnectionToTheIntegration passes = Passes(
			starting,
			new InMemorySettingsStore(ACabin.WithOneRoom()),
			reporter,
			ACabin.Outdoor);

		await passes.StartAsync(CancellationToken.None);

		starting.Runner.HandOver(starting.Core);
		await starting.TheModelComesUpAsync();

		Assert.True(
			await AnOpenConnection.UntilAsync(() => reporter.Cards.Raised.Count == 1),
			"The first pass over an open connection raised no card for two dead outdoor sensors.");

		// Gone and back with no pass in between, which is the shape a core restart takes: the loop sees no
		// transition at all, only a connection it has not read the house from before.
		starting.Runner.Lost();
		starting.Runner.HandOver(starting.Core);
		await starting.TheModelComesUpAsync();

		Assert.True(
			await AnOpenConnection.UntilAsync(() => reporter.Cards.Raised.Count == 2),
			"The card still standing was not raised again after the connection came back.");

		// The other direction, or the assertion above proves only that the card is raised on every pass: the
		// sensors are still out, and an entity change asking for another pass adds no third card.
		starting.Core.Reports(CoreUnderTest.Thermostat());

		Assert.False(
			await AnOpenConnection.UntilAsync(() => reporter.Cards.Raised.Count > 2),
			"A pass over the same connection raised the card a person can already see.");

		await passes.StopAsync(CancellationToken.None);
	}

	/// <summary>An outdoor sensor that has not reported for far longer than the freshness window, as a dead one reads.</summary>
	private static EntityState QuietSensor(string entityId, double temperature)
	{
		DateTimeOffset longAgo = DateTimeOffset.UtcNow - PlannerDefaults.Standard.SensorFreshness - TimeSpan.FromHours(1);

		return JsonSerializer.Deserialize<EntityState>(
			$$"""
			{
				"entity_id": "{{entityId}}",
				"state": "{{temperature.ToString(CultureInfo.InvariantCulture)}}",
				"last_changed": "{{longAgo:o}}",
				"last_updated": "{{longAgo:o}}",
				"attributes": { "friendly_name": "Outside", "unit_of_measurement": "°C" }
			}
			""")!;
	}

	private static ConnectionToTheIntegration Passes(
		AnOpenConnection open,
		ISettingsStore store,
		AReporter reporter,
		OutdoorChoice? outdoor = null)
	{
		TellsTheThermostats tells = new(store, open.Thermostats, NullLogger<TellsTheThermostats>.Instance);

		DrivesTheRooms driving = ADrivenCabin.Over(open, tells, reporter.Reports, outdoor: outdoor).Driver;

		HeatingDocumentHost document = AHostedDocument.Runnable().Host;

		return new ConnectionToTheIntegration(
			open.Connection,
			tells,
			driving,
			new TheCalendarsOnOffer(open.Model.About),
			tells,
			document,
			open.Model,
			open.Areas,
			new AreaDiscoveryScheduler(document, NullLogger<AreaDiscoveryScheduler>.Instance),
			open.RoomSettings,
			new RoomsThatCannotBeSetUp(null, reporter.Cards, NullLogger<RoomsThatCannotBeSetUp>.Instance),
			new NetworkSetClock(TimeProvider.System, DateTimeOffset.MinValue),
			TimeProvider.System,
			NullLogger<ConnectionToTheIntegration>.Instance);
	}
}
