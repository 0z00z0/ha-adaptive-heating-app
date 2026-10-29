using System.Text.Json;

using AdaptiveHeating.AddOn.Driving;
using AdaptiveHeating.AddOn.Integration;
using AdaptiveHeating.AddOn.Reporting;
using AdaptiveHeating.Page.Settings;
using AdaptiveHeating.Planner;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>
/// Every wall time the add-on decides on or shows is the household's, not the process's. An add-on container runs
/// on UTC whatever the household's zone is named, so a boundary and an expiry taken from the process land two hours
/// out in summer, and neither the log nor the card says which clock a time is on.
/// </summary>
public sealed class TheHouseholdClockTests
{
	/// <summary>Named, never taken from the machine: this box runs on it and the build server runs on UTC.</summary>
	private const string HouseholdZone = "Europe/Oslo";

	private static readonly DateTimeOffset EarlyMorning = new(2026, 9, 27, 5, 30, 0, TimeSpan.Zero);

	/// <summary>The moment the cabin drew as "Fri 22:05" while its own clock read five past midnight.</summary>
	private static readonly DateTimeOffset ExpiresAt = new(2026, 9, 25, 22, 5, 39, TimeSpan.Zero);

	[Fact]
	public async Task A_zone_that_cannot_be_resolved_raises_a_card_naming_it_and_is_never_taken_for_utc()
	{
		TheHouseholdClock unresolved = TheHouseholdClock.From(
			"Nowhere/Unresolvable",
			TimeZoneInfo.FindSystemTimeZoneById(HouseholdZone));

		Assert.False(unresolved.Resolved);
		Assert.Equal("Nowhere/Unresolvable", unresolved.Named);

		AReporter reporter = new(household: unresolved);
		IReadOnlyList<ACard> raised = await CardsFromOnePassAsync(reporter, unresolved);

		ACard card = Assert.Single(raised);

		Assert.Contains("Nowhere/Unresolvable", card.Message, StringComparison.Ordinal);
		Assert.Contains("wrong", card.Message, StringComparison.OrdinalIgnoreCase);

		// The other direction, without which the assertion above passes on a pass that raises a card for anything.
		TheHouseholdClock resolved = TheHouseholdClock.Of(TimeZoneInfo.FindSystemTimeZoneById(HouseholdZone));
		AReporter quiet = new(household: resolved);

		Assert.Empty(await CardsFromOnePassAsync(quiet, resolved));
		Assert.True(resolved.Resolved);
	}

	/// <summary>A zone nothing named is unresolved too, so a Supervisor that stopped setting one is not silence.</summary>
	[Fact]
	public void A_clock_given_no_zone_at_all_reports_itself_unresolved()
	{
		TheHouseholdClock nothingNamed = TheHouseholdClock.From(null, TimeZoneInfo.Utc);

		Assert.False(nothingNamed.Resolved);
		Assert.Null(nothingNamed.Named);
	}

	[Fact]
	public void Every_mode_and_every_source_a_person_reads_has_words_of_its_own()
	{
		// Enumerated rather than listed: a member added without words fails here, which the describing switch's
		// own catch-all arm would otherwise hide by describing it as a presence fault.
		foreach (HeatingMode mode in Enum.GetValues<HeatingMode>())
			Assert.NotNull(ReportWords.WordsFor(mode));

		foreach (TargetSource source in Enum.GetValues<TargetSource>())
			Assert.NotNull(ReportWords.WordsFor(source));
	}

	/// <summary>The words reach the line, rather than the member's own name reaching it.</summary>
	[Fact]
	public void A_source_reads_as_words_in_the_log_and_the_card()
	{
		TheHouseholdClock household = TheHouseholdClock.Of(TimeZoneInfo.Utc);

		ActivityLine holds = ReportWords.Describe(
			HeatingReport.About(WhatHappened.RoomHolds, EarlyMorning, ACabin.RoomId) with
			{
				Temperature = 21,
				Named = nameof(TargetSource.TimedHold)
			},
			"Kitchen",
			household);

		Assert.Equal("From a timed hold", holds.Why);
		Assert.DoesNotContain("TimedHold", holds.Why, StringComparison.Ordinal);
	}

	/// <summary>A deadline in the reporting words is read out on the household's clock, not the stored offset.</summary>
	[Fact]
	public void A_warm_up_deadline_is_read_out_on_the_household_clock()
	{
		ActivityLine line = ReportWords.Describe(
			HeatingReport.About(WhatHappened.WarmUpStarted, EarlyMorning, ACabin.RoomId) with
			{
				Temperature = 21,
				Deadline = ExpiresAt
			},
			"Kitchen",
			TheHouseholdClock.Of(TimeZoneInfo.FindSystemTimeZoneById(HouseholdZone)));

		Assert.Equal("by 00:05", line.Why);
	}

	private static async Task<IReadOnlyList<ACard>> CardsFromOnePassAsync(AReporter reporter, TheHouseholdClock household)
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync();

		InMemorySettingsStore store = new(ACabin.WithOneRoom());

		DrivesTheRooms driver = ADrivenCabin
			.Over(open, store, reporter.Reports, household: household)
			.Driver;

		_ = await driver.RunAsync(Thermostat(), EarlyMorning, CancellationToken.None);

		return reporter.Cards.Raised;
	}

	private static List<EntityState> Thermostat() =>
		JsonSerializer.Deserialize<List<EntityState>>(
			$$"""
			[{
				"entity_id": "{{ACabin.Thermostat}}",
				"state": "heat",
				"attributes": {
					"friendly_name": "Kitchen",
					"room_id": "{{ACabin.RoomId}}", "area_id": "{{ACabin.RoomId}}",
					"integration_version": "0.1.0",
					"vocabulary_version": 1,
					"temperature": 20,
					"current_temperature": 19,
					"hvac_action": "idle",
					"no_reading_at_all": false,
					"outdoor": -5,
					"target_set_at": "2026-09-26T08:00:00+00:00"
				}
			}]
			""")!;
}
