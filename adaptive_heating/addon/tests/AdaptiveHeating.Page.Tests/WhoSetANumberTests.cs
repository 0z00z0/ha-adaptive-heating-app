using System.Globalization;
using System.Text.Json;

using AdaptiveHeating.AddOn.Driving;
using AdaptiveHeating.AddOn.Settings;
using AdaptiveHeating.Page.Settings;
using AdaptiveHeating.Planner;

using AdaptiveLighting.Web.Presentation;

using NetDaemon.HassModel;

using Xunit;

using ChangeOrigin = AdaptiveHeating.Planner.ChangeOrigin;

namespace AdaptiveHeating.Page.Tests;

/// <summary>Who set a room's temperature, sorted as lighting sorts each change as it arrives, and what reads it.</summary>
public sealed class WhoSetANumberTests
{
	private const string UserId = "4f2a9c8e1b6d47f0a3c5e7b9d1f3a5c7";

	private static readonly DateTimeOffset Morning = ACabin.Typed.AddHours(1);

	[Fact]
	public async Task A_change_a_person_makes_at_the_thermostat_is_named_in_the_rooms_history_and_the_record()
	{
		(AnOpenConnection open, InMemorySettingsStore store, AReporter reporter, DrivesTheRooms driver) = await AlexSetsTheKitchenAsync();
		await using AnOpenConnection disposing = open;

		Assert.Equal("By Alex", store.Read().VerdictFor(ACabin.RoomId)?.History?.ChangedBy);

		RecordedReport manual = Assert.Single(
			((IActivityRecord)reporter.Reports).Read().Reports,
			report => report.Category == ActivityCategory.ManualChange);

		Assert.Equal("By Alex", manual.Why);

		// Renamed in Home Assistant: the room and the record name the person as they are called now.
		open.Core.House.Reports(Alex("Alexandra"));
		await driver.RunAsync(States(Thermostat(22.0, Morning.AddSeconds(30), userId: null)), Morning.AddMinutes(2), CancellationToken.None);

		Assert.Equal("By Alexandra", store.Read().VerdictFor(ACabin.RoomId)?.History?.ChangedBy);
		Assert.Equal(
			"By Alexandra",
			Assert.Single(((IActivityRecord)reporter.Reports).Read().Reports, report => report.Category == ActivityCategory.ManualChange).Why);
	}

	[Fact]
	public async Task A_change_an_automation_makes_is_kept_by_its_entity_id_and_named_as_it_is_called_now()
	{
		(AnOpenConnection open, InMemorySettingsStore store) = await CabinAsync(Automation("Warm mornings"));
		await using AnOpenConnection disposing = open;

		AReporter reporter = new(automationWords: open.Changes.AutomationWords);
		DrivesTheRooms driver = Driver(open, store, reporter);

		await driver.RunAsync(States(Thermostat(20.0, ACabin.Typed, userId: null)), Morning, CancellationToken.None);

		open.Core.House.SendEvent(
			"automation_triggered",
			new { entity_id = "automation.warm_mornings", name = "Warm mornings" },
			new Context { Id = "01JRUN" });
		open.Core.House.Reports(State(Thermostat(22.0, Morning.AddSeconds(30), userId: null, parentId: "01JRUN")));
		await driver.RunAsync(States(Thermostat(22.0, Morning.AddSeconds(30), userId: null)), Morning.AddMinutes(1), CancellationToken.None);

		AreaHistory history = Assert.IsType<AreaHistory>(store.Read().VerdictFor(ACabin.RoomId)?.History);

		Assert.Equal("automation.warm_mornings", history.ChangedByAutomationId);
		Assert.Equal("By automation: Warm mornings", history.ChangedBy);

		open.Core.House.Reports(Automation("Warmer mornings"));
		await driver.RunAsync(States(Thermostat(22.0, Morning.AddSeconds(30), userId: null)), Morning.AddMinutes(2), CancellationToken.None);

		Assert.Equal("By automation: Warmer mornings", store.Read().VerdictFor(ACabin.RoomId)?.History?.ChangedBy);
		Assert.Equal(
			"By automation: Warmer mornings",
			Assert.Single(((IActivityRecord)reporter.Reports).Read().Reports, report => report.Category == ActivityCategory.ManualChange).Why);
	}

	[Fact]
	public async Task The_record_of_what_is_in_force_says_a_hand_change_came_from_the_thermostat()
	{
		(AnOpenConnection open, InMemorySettingsStore store, _, _) = await AlexSetsTheKitchenAsync();
		await using AnOpenConnection disposing = open;

		Stamped<double> told = Assert.IsType<Stamped<double>>(store.Read().ToldFor(ACabin.RoomId));

		Assert.Equal(22.0, told.Value);
		Assert.Equal(ChangeOrigin.Thermostat, told.Origin);
	}

	[Fact]
	public async Task The_page_lists_a_room_set_by_hand_until_the_hold_runs_out()
	{
		(AnOpenConnection open, InMemorySettingsStore store, _, DrivesTheRooms driver) = await AlexSetsTheKitchenAsync();
		await using AnOpenConnection disposing = open;

		Assert.Equal(ACabin.RoomId, Assert.Single(store.Read().SetByHand).Id);

		// The default hold is 120 minutes.
		await driver.RunAsync(States(Thermostat(22.0, Morning.AddSeconds(30), userId: null)), Morning.AddHours(3), CancellationToken.None);

		Assert.Empty(store.Read().SetByHand);
		Assert.Equal(ChangeOrigin.SettingsPage, store.Read().ToldFor(ACabin.RoomId)?.Origin);
	}

	[Fact]
	public async Task After_a_restart_the_add_ons_own_last_command_is_not_read_as_a_hand()
	{
		(AnOpenConnection open, InMemorySettingsStore store) = await CabinAsync();
		await using AnOpenConnection disposing = open;

		// Set at the card before the temperatures were typed, so the mode wins and the add-on sends its own 20.
		await Driver(open, store, new AReporter())
			.RunAsync(States(Thermostat(18.0, ACabin.Typed.AddHours(-1), userId: null)), Morning, CancellationToken.None);

		Assert.Single(open.Core.Calls);

		// Somebody types 21 on the page while the add-on is down.
		RoomSlice kitchen = store.Read().Rooms[0];
		store.Receive(store.Read() with
		{
			Rooms =
			[
				kitchen with
				{
					ModeTemperatures = new Dictionary<HeatingMode, Stamped<double>>(kitchen.ModeTemperatures)
					{
						[HeatingMode.Home] = ACabin.Held(21, Morning.AddMinutes(2))
					}
				}
			]
		});

		// A new process over the same note. The add-on's own write carries a user this process has not learnt yet.
		AReporter restarted = new();

		await Driver(open, store, restarted)
			.RunAsync(States(Thermostat(20.0, Morning.AddSeconds(1), userId: "the-add-ons-own-user")), Morning.AddMinutes(5), CancellationToken.None);

		Assert.Equal(2, open.Core.Calls.Count);
		Assert.Null(store.Read().VerdictFor(ACabin.RoomId)?.History);
		Assert.DoesNotContain(
			((IActivityRecord)restarted.Reports).Read().Reports,
			report => report.Category == ActivityCategory.ManualChange);
	}

	[Fact]
	public void A_thermostat_reading_carries_the_context_the_change_arrived_under()
	{
		EntityState state = JsonSerializer.Deserialize<EntityState>(
			$$"""
			{
				"entity_id": "{{ACabin.Thermostat}}",
				"state": "heat",
				"attributes": { "room_id": "{{ACabin.RoomId}}", "area_id": "{{ACabin.RoomId}}", "integration_version": "0.1.0", "temperature": 21.0 },
				"context": { "id": "01JABCDEF", "parent_id": "01JPARENT", "user_id": "{{UserId}}" }
			}
			""")!;

		ThermostatNow thermostat = Assert.IsType<ThermostatNow>(ThermostatNow.From(state, ACabin.Typed));

		Assert.Equal("01JABCDEF", thermostat.Context?.Id);
		Assert.Equal("01JPARENT", thermostat.Context?.ParentId);
		Assert.Equal(UserId, thermostat.Context?.UserId);
	}

	// Alex sets the kitchen to 22 at the card; the integration's next write carries no user, which is the state a
	// pass reads. Only the change seen arriving still says who it was.
	private static async Task<(AnOpenConnection Open, InMemorySettingsStore Store, AReporter Reporter, DrivesTheRooms Driver)> AlexSetsTheKitchenAsync()
	{
		(AnOpenConnection open, InMemorySettingsStore store) = await CabinAsync(Alex());
		AReporter reporter = new(personWords: open.Changes.PersonWords);
		DrivesTheRooms driver = Driver(open, store, reporter);

		await driver.RunAsync(States(Thermostat(20.0, ACabin.Typed, userId: null)), Morning, CancellationToken.None);

		open.Core.House.Reports(State(Thermostat(22.0, Morning.AddSeconds(30), UserId)));

		await driver.RunAsync(States(Thermostat(22.0, Morning.AddSeconds(30), userId: null)), Morning.AddMinutes(1), CancellationToken.None);

		return (open, store, reporter, driver);
	}

	private static async Task<(AnOpenConnection Open, InMemorySettingsStore Store)> CabinAsync(params EntityState[] also)
	{
		AnOpenConnection open = await AnOpenConnection.StartAsync(
			house: [TheRoomsFollowTheHouseModeTests.SelectorOn("Normal", ACabin.Typed), .. also]);

		return (open, new InMemorySettingsStore(ACabin.WithOneRoom(away: 4, home: 20)));
	}

	private static DrivesTheRooms Driver(AnOpenConnection open, ISettingsStore store, AReporter reporter) =>
		ADrivenCabin.Over(
			open,
			store,
			reporter.Reports,
			outdoor: OutdoorChoice.Nothing,
			houseMode: TheRoomsFollowTheHouseModeTests.Cabin(),
			startsAt: Morning,
			rooms: new Adaptive.Settings.AreaConfig { AreaId = ACabin.RoomId }).Driver;

	private static EntityState Alex(string name = "Alex") =>
		State($$"""{ "entity_id": "person.alex", "state": "home", "attributes": { "user_id": "{{UserId}}", "friendly_name": "{{name}}" } }""");

	private static EntityState Automation(string name) =>
		State($$"""{ "entity_id": "automation.warm_mornings", "state": "on", "attributes": { "friendly_name": "{{name}}" } }""");

	private static EntityState State(string json) => JsonSerializer.Deserialize<EntityState>(json)!;

	private static List<EntityState> States(params string[] entities) =>
		JsonSerializer.Deserialize<List<EntityState>>($"[{string.Join(",", entities)}]")!;

	private static string Thermostat(double target, DateTimeOffset setAt, string? userId, string? parentId = null) =>
		$$"""
		{
			"entity_id": "{{ACabin.Thermostat}}",
			"state": "heat",
			"attributes": {
				"room_id": "{{ACabin.RoomId}}", "area_id": "{{ACabin.RoomId}}",
				"integration_version": "0.1.0",
				"temperature": {{target.ToString(CultureInfo.InvariantCulture)}},
				"current_temperature": 19.0,
				"hvac_action": "idle",
				"target_set_at": "{{setAt.ToString("O", CultureInfo.InvariantCulture)}}"
			},
			"context": { "id": "01JCHANGE", "parent_id": {{(parentId is null ? "null" : $"\"{parentId}\"")}}, "user_id": {{(userId is null ? "null" : $"\"{userId}\"")}} }
		}
		""";
}
