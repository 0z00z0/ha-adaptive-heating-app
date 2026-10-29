using System.Text.Json;

using AdaptiveHeating.AddOn.Integration;
using AdaptiveHeating.AddOn.Reporting;
using AdaptiveHeating.Planner;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>
/// Everything the add-on says used to go to the logger and nowhere else, so a restart lost it and nothing reached
/// a person. Both of those are what a household notices, so both are proved here.
/// </summary>
public sealed class WhatTheAddOnReportsTests
{
	private static readonly DateTimeOffset Morning = new(2026, 9, 25, 7, 0, 0, TimeSpan.Zero);

	[Fact]
	public void The_record_still_holds_what_happened_after_a_restart()
	{
		string directory = ACabin.ScratchDirectory();

		try
		{
			string document = Path.Combine(directory, "heating.json");

			// The run that did something.
			StateStoreRegistry first = new(document, NullLogger<StateStoreRegistry>.Instance);
			ActivityRecord before = new(new ActivityJournal(first, TimeProvider.System, NullLogger<ActivityJournal>.Instance));

			before.Record(HeatingReport.About(WhatHappened.Started, Morning));
			before.Record(HeatingReport.About(WhatHappened.RoomHolds, Morning.AddMinutes(1), ACabin.RoomId) with { Temperature = 20 });

			// The journal is written coalesced, so the flush on the way out is what a restart reads.
			first.Dispose();

			// The run after it, reading the same file and nothing else.
			StateStoreRegistry second = new(document, NullLogger<StateStoreRegistry>.Instance);
			ActivityRecord after = new(new ActivityJournal(second, TimeProvider.System, NullLogger<ActivityJournal>.Instance));

			ActivityTimeline restored = after.Read();

			Assert.Equal(2, restored.Entries.Count);
			Assert.Equal(2, restored.Newest);

			// Newest first, and each one is the report that was filed rather than a placeholder for it.
			Assert.Equal(WhatHappened.RoomHolds, restored.Entries[0].Report.What);
			Assert.Equal(ACabin.RoomId, restored.Entries[0].RoomId);
			Assert.Equal(20, restored.Entries[0].Report.Temperature);
			Assert.Equal(WhatHappened.Started, restored.Entries[1].Report.What);

			// The count carries on from where it stopped, so nothing written next reuses a number.
			after.Record(HeatingReport.About(WhatHappened.ModeInForce, Morning.AddMinutes(2)));
			Assert.Equal(3, after.Newest);

			second.Dispose();
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public async Task Every_report_reaches_the_journal_and_the_logbook_against_its_own_room()
	{
		AReporter reporter = new(thermostatOf: _ => ACabin.Thermostat);

		await reporter.Reports.ReportAsync(
			HeatingReport.About(WhatHappened.RoomHolds, Morning, ACabin.RoomId) with { Temperature = 20 },
			CancellationToken.None);

		// The add-on's own record keeps the report, as it did before the logbook was written to at all.
		Assert.Single(reporter.Record.Read().Entries);

		ALogbookLine line = Assert.Single(reporter.Logbook.Written);

		// The room's own entity, so the Activity panel beside that room shows the line and no other room's.
		Assert.Equal(ACabin.Thermostat, line.EntityId);
		Assert.Contains("20", line.What, StringComparison.Ordinal);

		// A house-wide report carries no entity, or it would file itself against a room it is not about.
		await reporter.Reports.ReportAsync(
			HeatingReport.About(WhatHappened.ModeInForce, Morning) with { Named = "Home" },
			CancellationToken.None);

		Assert.Null(reporter.Logbook.Written[1].EntityId);
	}

	[Fact]
	public async Task A_report_that_needs_a_person_reaches_Home_Assistant_as_a_card()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync();

		TellsAPerson person = new(open.Api, NullLogger<TellsAPerson>.Instance);

		ReportsWhatHappened reports = new(
			new ActivityRecord(),
			person,
			new TellsTheLogbook(open.Api, NullLogger<TellsTheLogbook>.Instance),
			// A display name that shares no word with the room's id, so a card built from the id could not pass.
			roomId => roomId == ACabin.RoomId ? "Scullery" : roomId,
			roomId => ACabin.Thermostat,
			roomId => roomId,
			TheHouseholdClock.Of(TimeZoneInfo.Utc),
			userId => userId,
			entityId => entityId,
			NullLogger<ReportsWhatHappened>.Instance);

		// A room that cannot reach its deadline is something the add-on cannot put right itself.
		await reports.ReportAsync(
			HeatingReport.About(WhatHappened.CannotWarmInTime, Morning, ACabin.RoomId) with
			{
				Temperature = 20,
				Against = 18,
				Deadline = Morning.AddHours(2)
			},
			CancellationToken.None);

		// The line into the logbook went out beside the card, and the card is what this one is about.
		Assert.Equal(2, open.Core.Calls.Count);

		SentCall raised = Assert.Single(
			open.Core.Calls,
			call => string.Equals(call.Action, $"{TellsAPerson.Domain}.{TellsAPerson.Create}", StringComparison.Ordinal));

		// Read as fields, not as a substring of the message: the writer escapes the degree sign, so a search for
		// the words as they are written would pass or fail on the encoder rather than on the card.
		using JsonDocument sent = JsonDocument.Parse(raised.Json);
		JsonElement fields = sent.RootElement.GetProperty("service_data");

		// The room is named as a person calls it, and the words say what is wrong and what it does reach.
		Assert.Equal("Scullery cannot reach 20 °C", fields.GetProperty("title").GetString());
		Assert.Equal("Reaches 18 °C by 09:00", fields.GetProperty("message").GetString());

		// The id is the kind and the room id, so the same problem replaces its own card instead of stacking one.
		Assert.Equal("adaptive_heating_cannot_warm_in_time_kitchen", fields.GetProperty("notification_id").GetString());

		// No words a person reads name the room by its identifier.
		Assert.DoesNotContain(ACabin.RoomId, fields.GetProperty("title").GetString(), StringComparison.Ordinal);
		Assert.DoesNotContain(ACabin.RoomId, fields.GetProperty("message").GetString(), StringComparison.Ordinal);

		// An ordinary report is filed and raises nothing: a card for every temperature trains a person to
		// dismiss them.
		await reports.ReportAsync(
			HeatingReport.About(WhatHappened.RoomHolds, Morning, ACabin.RoomId) with { Temperature = 20 },
			CancellationToken.None);

		// A line of its own in the logbook, and still the one card.
		Assert.Equal(3, open.Core.Calls.Count);

		Assert.Single(
			open.Core.Calls,
			call => string.Equals(call.Action, $"{TellsAPerson.Domain}.{TellsAPerson.Create}", StringComparison.Ordinal));

		Assert.Equal(2, reports.Recent().Entries.Count);
	}

	[Fact]
	public async Task A_new_temperature_or_a_renamed_room_replaces_the_rooms_card()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync();

		string roomName = "Scullery";

		ReportsWhatHappened reports = new(
			new ActivityRecord(),
			new TellsAPerson(open.Api, NullLogger<TellsAPerson>.Instance),
			new TellsTheLogbook(open.Api, NullLogger<TellsTheLogbook>.Instance),
			roomId => roomName,
			roomId => ACabin.Thermostat,
			roomId => roomId,
			TheHouseholdClock.Of(TimeZoneInfo.Utc),
			userId => userId,
			entityId => entityId,
			NullLogger<ReportsWhatHappened>.Instance);

		await reports.ReportAsync(
			HeatingReport.About(WhatHappened.CannotWarmInTime, Morning, ACabin.RoomId) with { Temperature = 20, Against = 18 },
			CancellationToken.None);

		roomName = "Pantry";

		await reports.ReportAsync(
			HeatingReport.About(WhatHappened.CannotWarmInTime, Morning, ACabin.RoomId) with { Temperature = 21, Against = 18 },
			CancellationToken.None);

		string?[] ids =
		[
			.. open.Core.Calls
				.Where(call => string.Equals(call.Action, $"{TellsAPerson.Domain}.{TellsAPerson.Create}", StringComparison.Ordinal))
				.Select(call => JsonDocument.Parse(call.Json).RootElement.GetProperty("service_data").GetProperty("notification_id").GetString())
		];

		// Two cards raised, one id: the second replaces the first, and no card stays behind under the old name.
		Assert.Equal(2, ids.Length);
		Assert.Equal(ids[0], ids[1]);
	}
}
