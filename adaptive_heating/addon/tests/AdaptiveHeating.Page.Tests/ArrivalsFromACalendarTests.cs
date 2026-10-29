using System.Globalization;
using System.Text.Json;

using AdaptiveHeating.AddOn.Driving;
using AdaptiveHeating.AddOn.Integration;
using AdaptiveHeating.Page.Settings;
using AdaptiveHeating.Planner;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>
/// A planned arrival is what a warm-up works backwards from, and a calendar's state names only the nearest event.
/// So the arrivals are read over a stretch of time, and a cabin with two visits ahead of it sees both.
/// </summary>
public sealed class ArrivalsFromACalendarTests
{
	private static readonly DateTimeOffset Morning = new(2026, 9, 24, 7, 0, 0, TimeSpan.Zero);

	[Fact]
	public async Task Two_arrivals_in_the_window_are_both_seen_and_the_nearer_one_is_the_deadline()
	{
		DateTimeOffset nearer = Morning.AddDays(2);
		DateTimeOffset further = Morning.AddDays(5);

		ACalendar calendar = ACalendar.Holding(further, nearer);

		await using AnOpenConnection open = await AnOpenConnection.StartAsync((_, call) => calendar.Answer(call));

		ADrivenCabin cabin = ADrivenCabin.Over(open, new InMemorySettingsStore(Planning()), new AReporter().Reports);

		DrivingPass pass = await cabin.Driver.RunAsync(States(Thermostat(), TheCalendars()), Morning, CancellationToken.None);

		// Both arrivals came back, which the entity's own state could never have shown.
		Assert.Equal([nearer, further], cabin.Arrivals.Window.Arrivals);

		// The nearer one is what a warm-up is planned against.
		Assert.Equal(nearer, pass.NextArrival);
	}

	[Fact]
	public async Task An_arrival_beyond_the_window_is_no_deadline_and_one_inside_it_is()
	{
		DateTimeOffset inside = Morning.AddDays(3);
		DateTimeOffset beyond = Morning.AddDays(20);

		ACalendar calendar = ACalendar.Holding(inside, beyond);

		await using AnOpenConnection open = await AnOpenConnection.StartAsync((_, call) => calendar.Answer(call));

		ADrivenCabin cabin = ADrivenCabin.Over(open, new InMemorySettingsStore(Planning()), new AReporter().Reports);

		DrivingPass pass = await cabin.Driver.RunAsync(States(Thermostat(), TheCalendars()), Morning, CancellationToken.None);

		// The stretch the add-on asked for is what left the distant one out, not anything the script decided.
		(DateTimeOffset from, DateTimeOffset until) = Assert.Single(calendar.Reads);

		Assert.Equal(Morning, from);
		Assert.Equal(ArrivalWindow.Length, until - from);

		Assert.Equal([inside], cabin.Arrivals.Window.Arrivals);
		Assert.Equal(inside, pass.NextArrival);
	}

	[Fact]
	public async Task A_whole_day_entry_carries_no_time_and_is_no_arrival()
	{
		DateTimeOffset day = Morning.AddDays(3);

		await using AnOpenConnection open = await AnOpenConnection.StartAsync(
			(_, call) => string.Equals(call.Action, $"{CalendarActions.Domain}.{CalendarActions.GetEvents}", StringComparison.Ordinal)
				? ACalendar.ReadAnswering(call.Id, ACalendar.Plan, ACalendar.WholeDay(day))
				: CoreUnderTest.Worked(call.Id));

		ADrivenCabin cabin = ADrivenCabin.Over(open, new InMemorySettingsStore(Planning()), new AReporter().Reports);

		DrivingPass pass = await cabin.Driver.RunAsync(States(Thermostat(), TheCalendars()), Morning, CancellationToken.None);

		// Reading a whole day as midnight would start the heating the night before.
		Assert.Empty(cabin.Arrivals.Window.Arrivals);
		Assert.Null(pass.NextArrival);
	}

	[Fact]
	public async Task A_room_starts_heating_early_enough_for_the_arrival_and_asks_once()
	{
		// The room reads 16 and its home temperature is 20. On four fifths of the half a degree an hour it
		// starts on, four degrees is ten hours.
		DateTimeOffset arrival = Morning.AddHours(10);
		ACalendar calendar = ACalendar.Holding(arrival);

		await using AnOpenConnection tooEarly = await AnOpenConnection.StartAsync((_, call) => calendar.Answer(call));

		ADrivenCabin before = ADrivenCabin.Over(tooEarly, new InMemorySettingsStore(Planning()), new AReporter().Reports);

		await before.Driver.RunAsync(States(Thermostat(), TheCalendars()), Morning.AddMinutes(-30), CancellationToken.None);

		Assert.DoesNotContain(tooEarly.Core.Calls, call =>
			call.Action.EndsWith(HeatingActions.WarmRoomBy, StringComparison.Ordinal));

		await using AnOpenConnection open = await AnOpenConnection.StartAsync((_, call) => calendar.Answer(call));

		ADrivenCabin cabin = ADrivenCabin.Over(open, new InMemorySettingsStore(Planning()), new AReporter().Reports);

		await cabin.Driver.RunAsync(States(Thermostat(), TheCalendars()), Morning, CancellationToken.None);

		SentCall asked = Assert.Single(open.Core.Calls, call =>
			call.Action.EndsWith(HeatingActions.WarmRoomBy, StringComparison.Ordinal));

		Assert.Contains("\"temperature\":20", asked.Json, StringComparison.Ordinal);
		Assert.Contains("2026-09-24T17:00:00", asked.Json, StringComparison.Ordinal);

		// The same arrival a minute later is the same arrival, so nothing is asked for again.
		await cabin.Driver.RunAsync(States(Thermostat(), TheCalendars()), Morning.AddMinutes(1), CancellationToken.None);

		Assert.Single(open.Core.Calls, call => call.Action.EndsWith(HeatingActions.WarmRoomBy, StringComparison.Ordinal));
	}

	[Fact]
	public async Task A_pass_carries_on_when_no_calendar_is_chosen()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync();

		AReporter reporter = new();
		ADrivenCabin cabin = ADrivenCabin.Over(open, new InMemorySettingsStore(ACabin.WithOneRoom()), reporter.Reports);

		DrivingPass pass = await cabin.Driver.RunAsync(States(Thermostat(), TheCalendars()), Morning, CancellationToken.None);

		// The room is still driven, and nothing was asked of a calendar at all.
		Assert.Equal(1, pass.RoomsDriven);
		Assert.Null(pass.NextArrival);
		Assert.Empty(reporter.Cards.Raised);

		Assert.DoesNotContain(open.Core.Calls, call =>
			call.Action.StartsWith(CalendarActions.Domain, StringComparison.Ordinal));

		Assert.Contains(open.Core.Calls, call =>
			call.Action.EndsWith(HeatingActions.SetTemperature, StringComparison.Ordinal));
	}

	[Fact]
	public async Task A_chosen_calendar_that_is_not_there_is_said_once_and_the_pass_carries_on()
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync();

		AReporter reporter = new();
		ADrivenCabin cabin = ADrivenCabin.Over(open, new InMemorySettingsStore(Planning()), reporter.Reports);

		// The house holds no calendar at all, so the one the settings name is not there.
		DrivingPass pass = await cabin.Driver.RunAsync(States(Thermostat()), Morning, CancellationToken.None);

		Assert.Equal(1, pass.RoomsDriven);
		Assert.Null(pass.NextArrival);
		Assert.Single(reporter.Cards.Raised);

		Assert.DoesNotContain(open.Core.Calls, call =>
			call.Action.StartsWith(CalendarActions.Domain, StringComparison.Ordinal));

		Assert.Contains(open.Core.Calls, call =>
			call.Action.EndsWith(HeatingActions.SetTemperature, StringComparison.Ordinal));

		// Said once while it stays true, not once a minute.
		await cabin.Driver.RunAsync(States(Thermostat()), Morning.AddMinutes(1), CancellationToken.None);

		Assert.Single(reporter.Cards.Raised);
	}

	/// <summary>One room, with both calendars chosen by entity id.</summary>
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
