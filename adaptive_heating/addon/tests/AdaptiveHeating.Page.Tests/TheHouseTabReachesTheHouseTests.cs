using System.Globalization;
using System.Text.Json;

using Adaptive.Engine;
using Adaptive.Settings;
using Adaptive.UI;

using AdaptiveHeating.AddOn.Driving;
using AdaptiveHeating.AddOn.Settings;
using AdaptiveHeating.Page.Settings;
using AdaptiveHeating.Planner;

using AdaptiveLighting.Abstractions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>What the House tab sets has to reach the house, or a person turns a knob that does nothing.</summary>
public sealed class TheHouseTabReachesTheHouseTests
{
	// After the room's mode temperatures were typed, so a hand change made now is the newer of the two.
	private static readonly DateTimeOffset Morning = ACabin.Typed.AddHours(1);

	private const string Gate = "input_boolean.heating_on";

	[Fact]
	public async Task The_master_switch_pauses_every_command()
	{
		await using AnOpenConnection paused = await AnOpenConnection.StartAsync(
			house: [TheRoomsFollowTheHouseModeTests.SelectorOn("Borte", Morning), CoreUnderTest.State(Gate, "off", "{}")]);

		await Driver(paused, global => global.GateEntity = Gate).RunAsync(States(Thermostat(20.0)), Morning, CancellationToken.None);

		Assert.Empty(paused.Core.Calls);

		// The control: the same house with the switch on is commanded.
		await using AnOpenConnection running = await AnOpenConnection.StartAsync(
			house: [TheRoomsFollowTheHouseModeTests.SelectorOn("Borte", Morning), CoreUnderTest.State(Gate, "on", "{}")]);

		await Driver(running, global => global.GateEntity = Gate).RunAsync(States(Thermostat(20.0)), Morning, CancellationToken.None);

		Assert.Single(running.Core.Calls);
	}

	[Fact]
	public async Task A_target_landing_inside_the_echo_window_is_the_commands_own()
	{
		Assert.Equal(4.0, await HeldAfterAClampedEcho(echoWindowSeconds: 8));

		// The control: with no window the clamped value reads as somebody at the thermostat.
		Assert.Equal(5.0, await HeldAfterAClampedEcho(echoWindowSeconds: 0));
	}

	[Fact]
	public async Task An_automations_change_is_a_hand_only_where_the_house_says_so()
	{
		Assert.Equal(2, await CallsAfterAnAutomation(automationsAreHands: false));

		// The control: counted as a hand, the change stands and nothing is sent back.
		Assert.Equal(1, await CallsAfterAnAutomation(automationsAreHands: true));
	}

	[Fact]
	public void The_log_level_is_applied_while_running()
	{
		HeatingConfig config = HeatingConfig.CreateDefault();
		config.Global.LogLevel = "Warning";

		ALogLevelControl control = new();
		HeatingDocumentHost host = Host(config, logLevel: control);

		host.Reload();

		Assert.Equal(LogLevel.Warning, control.Applied);
		Assert.Equal(0, control.Restored);

		HeatingConfig cleared = host.Store.Load();
		cleared.Global.LogLevel = null;

		Assert.True(host.Save(cleared).Written);
		Assert.Equal(1, control.Restored);
	}

	[Fact]
	public void The_house_card_writes_a_live_option_and_refuses_any_other()
	{
		HeatingConfig config = HeatingConfig.CreateDefault();
		config.Global.HouseMode = new HouseModeConfig { Entity = "input_select.husmodus" };

		HeatingDocumentHost host = Host(config);
		host.Reload();

		AHouseUnderTest house = new(CoreUnderTest.State("input_select.husmodus", "Hjemme", """{ "options": ["Hjemme", "Borte"] }"""));
		ModeService modes = new(() => house, new HaCatalog(() => house, () => house, NullLoggerFactory.Instance), host, NullLogger<ModeService>.Instance);

		Assert.False(modes.SelectHouseMode("Ski"));
		Assert.Empty(house.Calls);

		Assert.True(modes.SelectHouseMode("Borte"));

		ModelCall call = Assert.Single(house.Calls);
		Assert.Equal("input_select", call.Domain);
		Assert.Equal("select_option", call.Service);
		Assert.Equal(["input_select.husmodus"], call.Target?.EntityIds);
	}

	private static async Task<double> HeldAfterAClampedEcho(int echoWindowSeconds)
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync(
			house: [TheRoomsFollowTheHouseModeTests.SelectorOn("Borte", Morning)]);

		DrivesTheRooms driver = Driver(open, global => global.SelfEchoWindowSeconds = echoWindowSeconds);

		await driver.RunAsync(States(Thermostat(20.0)), Morning, CancellationToken.None);

		// The thermostat took the 4 °C asked for as 5 °C, three seconds after it was sent.
		await driver.RunAsync(States(Thermostat(5.0, setAt: Morning.AddSeconds(3))), Morning.AddMinutes(1), CancellationToken.None);

		// Never sent again either way: neither a landed echo nor a hand change is a reason to command.
		Assert.Single(open.Core.Calls);

		return driver.InForce.Temperatures[ACabin.RoomId].Value;
	}

	private static async Task<int> CallsAfterAnAutomation(bool automationsAreHands)
	{
		await using AnOpenConnection open = await AnOpenConnection.StartAsync(
			house: [TheRoomsFollowTheHouseModeTests.SelectorOn("Borte", Morning)]);

		DrivesTheRooms driver = Driver(open, global => global.TreatAutomationsAsManual = automationsAreHands);

		await driver.RunAsync(States(Thermostat(20.0)), Morning, CancellationToken.None);

		// Another automation set 21 °C half a minute later: its context carries a parent.
		await driver.RunAsync(
			States(Thermostat(21.0, setAt: Morning.AddSeconds(30), parentContext: "01JAUTOMATION")),
			Morning.AddMinutes(1),
			CancellationToken.None);

		return open.Core.Calls.Count;
	}

	private static DrivesTheRooms Driver(AnOpenConnection open, Action<GlobalConfig> global) =>
		ADrivenCabin.Over(
			open,
			new InMemorySettingsStore(ACabin.WithOneRoom(away: 4, home: 20)),
			new AReporter().Reports,
			outdoor: OutdoorChoice.Nothing,
			houseMode: TheRoomsFollowTheHouseModeTests.Cabin(),
			startsAt: Morning,
			global: global).Driver;

	private static HeatingDocumentHost Host(
		HeatingConfig config,
		ILogLevelControl? logLevel = null)
	{
		string path = Path.Combine(ACabin.ScratchDirectory(), "AdaptiveHeating.yaml");
		File.WriteAllText(path, HeatingConfigDocument.Serialize(config));

		return new HeatingDocumentHost(
			new HeatingConfigStore(path, NullLogger<HeatingConfigStore>.Instance),
			_ => ValidationContext.None,
			() => { },
			NullLogger<HeatingDocumentHost>.Instance,
			logLevel);
	}

	private static List<EntityState> States(params string[] entities) =>
		JsonSerializer.Deserialize<List<EntityState>>($"[{string.Join(",", entities)}]")!;

	private static string Thermostat(double target, DateTimeOffset? setAt = null, string? parentContext = null) =>
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
				"target_set_at": "{{(setAt ?? new DateTimeOffset(2026, 9, 23, 8, 0, 0, TimeSpan.Zero)).ToString("O", CultureInfo.InvariantCulture)}}"
			},
			"context": { "id": "01JCHANGE", "parent_id": {{(parentContext is null ? "null" : $"\"{parentContext}\"")}}, "user_id": null }
		}
		""";

	private sealed class ALogLevelControl : ILogLevelControl
	{
		public LogLevel? Applied { get; private set; }

		public int Restored { get; private set; }

		public bool IsSupported => true;

		public void Apply(LogLevel level) => Applied = level;

		public void RestoreInitial() => Restored++;
	}
}
