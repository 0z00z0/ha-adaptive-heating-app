using System.Globalization;
using System.Text.Json;

using AdaptiveHeating.AddOn.Driving;
using AdaptiveHeating.AddOn.Integration;
using AdaptiveHeating.Page.Settings;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>
/// Nothing can take an entry out of a calendar again, so a warm-up written down twice is wrong for ever. What
/// was asked for is kept in a note until its entry is in the calendar, and the note is what a restart reads.
/// </summary>
public sealed class TheRecordOfWarmUpsTests
{
	private static readonly DateTimeOffset Morning = new(2026, 9, 24, 7, 0, 0, TimeSpan.Zero);

	/// <summary>Four degrees at four fifths of half a degree an hour is ten hours, so the arrival is at 17:00.</summary>
	private static readonly DateTimeOffset Arrival = Morning.AddHours(10);

	[Fact]
	public async Task A_finished_warm_up_is_written_down_once_and_seeing_it_again_writes_nothing()
	{
		ACalendar calendar = ACalendar.Holding(Arrival);
		ANote note = new();
		InMemorySettingsStore store = new(Planning());

		await using AnOpenConnection open = await AnOpenConnection.StartAsync((_, call) => calendar.Answer(call));

		ADrivenCabin asking = ADrivenCabin.Over(open, store, new AReporter().Reports, note: note, nameOf: _ => "Kitchen");

		await asking.Driver.RunAsync(States(Thermostat(), TheCalendars()), Morning, CancellationToken.None);

		// The warm-up was asked for and is now waiting for its deadline to come round.
		Assert.Single(asking.Record.Waiting);
		Assert.Empty(calendar.Written);

		// A restart between the asking and the deadline: the note is all that carries the warm-up across it.
		ADrivenCabin afterTheRestart = ADrivenCabin.Over(open, store, new AReporter().Reports, note: note, nameOf: _ => "Kitchen");

		Assert.Single(afterTheRestart.Record.Waiting);

		DrivingPass pass = await afterTheRestart.Driver.RunAsync(
			States(Thermostat(current: 20.2, target: 20.0), TheCalendars()),
			Arrival.AddMinutes(1),
			CancellationToken.None);

		Assert.Equal(1, pass.Recorded);

		(string summary, string description, DateTimeOffset from, DateTimeOffset until) = Assert.Single(calendar.Written);

		Assert.Equal("Kitchen warm-up met", summary);
		Assert.Equal("Wanted 20 °C, reached 20.2 °C", description);
		Assert.Equal(Morning, from);
		Assert.Equal(Arrival, until);

		Assert.Empty(afterTheRestart.Record.Waiting);

		// The same warm-up a minute later, and after another restart reading the same note: nothing more is written.
		await afterTheRestart.Driver.RunAsync(
			States(Thermostat(current: 20.2, target: 20.0), TheCalendars()),
			Arrival.AddMinutes(2),
			CancellationToken.None);

		ADrivenCabin later = ADrivenCabin.Over(open, store, new AReporter().Reports, note: note, nameOf: _ => "Kitchen");

		Assert.Empty(later.Record.Waiting);

		await later.Driver.RunAsync(
			States(Thermostat(current: 20.2, target: 20.0), TheCalendars()),
			Arrival.AddMinutes(3),
			CancellationToken.None);

		Assert.Single(calendar.Written);
	}

	[Fact]
	public async Task A_room_that_did_not_reach_the_temperature_says_so()
	{
		ACalendar calendar = ACalendar.Holding(Arrival);
		ANote note = new();
		InMemorySettingsStore store = new(Planning());

		await using AnOpenConnection open = await AnOpenConnection.StartAsync((_, call) => calendar.Answer(call));

		ADrivenCabin cabin = ADrivenCabin.Over(open, store, new AReporter().Reports, note: note, nameOf: _ => "Kitchen");

		await cabin.Driver.RunAsync(States(Thermostat(), TheCalendars()), Morning, CancellationToken.None);

		await cabin.Driver.RunAsync(
			States(Thermostat(current: 18.4, target: 20.0), TheCalendars()),
			Arrival.AddMinutes(1),
			CancellationToken.None);

		(string summary, string description, _, _) = Assert.Single(calendar.Written);

		Assert.Equal("Kitchen warm-up missed", summary);
		Assert.Equal("Wanted 20 °C, reached 18.4 °C", description);
	}

	[Fact]
	public async Task With_no_record_calendar_a_finished_warm_up_writes_nothing_and_says_so_once()
	{
		ACalendar calendar = ACalendar.Holding(Arrival);
		ANote note = new();
		AReporter reporter = new();

		// The arrivals are read from a calendar that is there; the record names none at all.
		InMemorySettingsStore store = new(Planning() with { RecordCalendar = null });

		await using AnOpenConnection open = await AnOpenConnection.StartAsync((_, call) => calendar.Answer(call));

		ADrivenCabin cabin = ADrivenCabin.Over(open, store, reporter.Reports, note: note);

		await cabin.Driver.RunAsync(States(Thermostat(), TheCalendars()), Morning, CancellationToken.None);

		Assert.Single(cabin.Record.Waiting);

		DrivingPass pass = await cabin.Driver.RunAsync(
			States(Thermostat(current: 20.2, target: 20.0), TheCalendars()),
			Arrival.AddMinutes(1),
			CancellationToken.None);

		// Nothing written, the pass still ran, and the room is still being driven.
		Assert.Equal(0, pass.Recorded);
		Assert.Equal(1, pass.RoomsDriven);
		Assert.Empty(calendar.Written);
		Assert.Single(reporter.Cards.Raised);
		Assert.Empty(cabin.Record.Waiting);

		// Said once. A second finished warm-up raises no second card while there is still nowhere to write one.
		cabin.Record.Asked(ACabin.RoomId, 20.0, Arrival.AddHours(1), Morning);

		await cabin.Driver.RunAsync(
			States(Thermostat(current: 20.2, target: 20.0), TheCalendars()),
			Arrival.AddHours(2),
			CancellationToken.None);

		Assert.Single(reporter.Cards.Raised);
		Assert.Empty(calendar.Written);
	}

	[Fact]
	public async Task A_record_calendar_that_is_not_there_writes_nothing_and_the_pass_carries_on()
	{
		ACalendar calendar = ACalendar.Holding(Arrival);
		ANote note = new();
		AReporter reporter = new();

		await using AnOpenConnection open = await AnOpenConnection.StartAsync((_, call) => calendar.Answer(call));

		ADrivenCabin cabin = ADrivenCabin.Over(open, new InMemorySettingsStore(Planning()), reporter.Reports, note: note);

		await cabin.Driver.RunAsync(States(Thermostat(), TheCalendars()), Morning, CancellationToken.None);

		Assert.Single(cabin.Record.Waiting);

		// The record calendar has gone from Home Assistant, while the arrivals calendar is still reported.
		DrivingPass pass = await cabin.Driver.RunAsync(
			States(Thermostat(current: 20.2, target: 20.0), ACalendar.Entity(ACalendar.Plan, "Arrivals")),
			Arrival.AddMinutes(1),
			CancellationToken.None);

		Assert.Equal(0, pass.Recorded);
		Assert.Equal(1, pass.RoomsDriven);
		Assert.Empty(calendar.Written);
		Assert.Single(reporter.Cards.Raised);

		Assert.Contains(open.Core.Calls, call =>
			call.Action.EndsWith(HeatingActions.SetTemperature, StringComparison.Ordinal));
	}

	private static HeatingDocument Planning() =>
		ACabin.WithOneRoom() with
		{
			ArrivalCalendar = ACalendar.Plan,
			RecordCalendar = ACalendar.Record
		};

	private static List<EntityState> States(params string[] entities) =>
		JsonSerializer.Deserialize<List<EntityState>>($"[{string.Join(",", entities)}]")!;

	private static string TheCalendars() =>
		$"{ACalendar.Entity(ACalendar.Plan, "Arrivals")},{ACalendar.Entity(ACalendar.Record, "Heating record")}";

	private static string Thermostat(double current = 16.0, double target = 16.0) =>
		$$"""
		{
			"entity_id": "{{ACabin.Thermostat}}",
			"state": "heat",
			"attributes": {
				"friendly_name": "Kitchen",
				"room_id": "{{ACabin.RoomId}}", "area_id": "{{ACabin.RoomId}}",
				"integration_version": "0.1.0",
				"vocabulary_version": 1,
				"temperature": {{Invariant(target)}},
				"current_temperature": {{Invariant(current)}},
				"hvac_action": "idle",
				"no_reading_at_all": false,
				"outdoor": -5,
				"target_set_at": "2026-09-23T08:00:00+00:00"
			}
		}
		""";

	// A temperature written the Norwegian way is not a number to a JSON reader.
	private static string Invariant(double value) => value.ToString(CultureInfo.InvariantCulture);
}
