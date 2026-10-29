using Adaptive.Engine;
using Adaptive.Settings;

using AdaptiveHeating.AddOn.Integration;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>Which power sensor a room uses: the heater's device, then the label, and a pick over both.</summary>
public sealed class EachRoomFindsItsPowerSensorTests
{
	[Fact]
	public void A_sensor_on_the_heaters_device_wins_over_a_labelled_one()
	{
		ResolvedRoom room = Resolve(
			[
				ARecordedHouse.Heater("switch.stue", "stue") with { DeviceId = "plug" },
				Meter("sensor.plug_power", "stue", []) with { DeviceId = "plug" },
				Meter("sensor.stue_power", "stue", [ARecordedHouse.Heating])
			]);

		Assert.Equal("sensor.plug_power", room.PowerSensor);
		Assert.Equal(PowerSensorSource.SameDevice, room.PowerSensorFoundBy);
	}

	[Fact]
	public void A_labelled_sensor_in_the_area_is_used_when_no_heater_carries_one()
	{
		ResolvedRoom room = Resolve(
			[
				ARecordedHouse.Heater("switch.stue", "stue") with { DeviceId = "relay" },
				Meter("sensor.stue_spare", "stue", []),
				Meter("sensor.stue_power", "stue", [ARecordedHouse.Heating])
			]);

		Assert.Equal("sensor.stue_power", room.PowerSensor);
		Assert.Equal(PowerSensorSource.Label, room.PowerSensorFoundBy);
	}

	[Fact]
	public void A_picked_sensor_overrides_both_steps()
	{
		ResolvedRoom room = Resolve(
			[
				ARecordedHouse.Heater("switch.stue", "stue") with { DeviceId = "plug" },
				Meter("sensor.plug_power", "stue", []) with { DeviceId = "plug" },
				Meter("sensor.stue_power", "stue", [ARecordedHouse.Heating])
			],
			new AreaConfig { AreaId = "stue", PowerSensor = "sensor.elsewhere_power" });

		Assert.Equal("sensor.elsewhere_power", room.PowerSensor);
		Assert.Equal(PowerSensorSource.Chosen, room.PowerSensorFoundBy);
	}

	[Fact]
	public void Two_candidates_in_one_step_give_none_and_one_warning_until_one_is_picked()
	{
		ARecordedHouse house = new(
			[ARecordedHouse.Area("stue")],
			[
				ARecordedHouse.Heater("switch.stue", "stue"),
				Meter("sensor.stue_a_power", "stue", [ARecordedHouse.Heating]),
				Meter("sensor.stue_b_power", "stue", [ARecordedHouse.Heating])
			]);

		HeatingConfig document = HeatingConfig.CreateDefault();
		document.Areas.Add(new AreaConfig { AreaId = "stue" });

		Assert.True(house.Resolver().TryResolve(document.Areas[0], document.Defaults, out ResolvedRoom? room, out _));
		Assert.Null(room.PowerSensor);
		Assert.Equal(PowerSensorSource.MoreThanOne, room.PowerSensorFoundBy);
		Assert.Equal(["sensor.stue_a_power", "sensor.stue_b_power"], room.PowerSensorCandidates);

		PowerSensorsToPick warnings = new(NullLogger.Instance);

		Assert.Single(warnings.Check(document, house.Resolver()));
		Assert.Empty(warnings.Check(document, house.Resolver()));

		document.Areas[0].PowerSensor = "sensor.stue_a_power";

		Assert.Empty(warnings.Check(document, house.Resolver()));
	}

	[Fact]
	public void Hidden_sensors_and_this_integrations_own_are_never_candidates()
	{
		ResolvedRoom room = Resolve(
			[
				ARecordedHouse.Heater("switch.stue", "stue") with { DeviceId = "plug" },
				Meter("sensor.plug_power", "stue", []) with { DeviceId = "plug", Hidden = true },
				Meter("sensor.stue_hidden_power", "stue", [ARecordedHouse.Heating]) with { Hidden = true },
				new RecordedEntity("sensor.stue_room_power", "stue", [ARecordedHouse.Heating], ARecordedHouse.Ours) { DeviceClass = "power" }
			]);

		Assert.Null(room.PowerSensor);
		Assert.Equal(PowerSensorSource.None, room.PowerSensorFoundBy);
	}

	[Fact]
	public void A_room_over_a_thermostat_finds_the_sensor_on_that_thermostats_device()
	{
		ResolvedRoom room = Resolve(
			[
				ARecordedHouse.Heater("climate.stue_panel", "stue") with { DeviceId = "panel" },
				Meter("sensor.panel_power", "stue", []) with { DeviceId = "panel" },
				ARecordedHouse.OurThermostat("climate.stue", "stue")
			]);

		Assert.Equal(["climate.stue_panel"], room.Heaters);
		Assert.Equal("sensor.panel_power", room.PowerSensor);
		Assert.Equal(PowerSensorSource.SameDevice, room.PowerSensorFoundBy);
	}

	[Fact]
	public async Task A_found_sensor_leaves_on_the_regulation_action()
	{
		ARecordedHouse house = new(
			[ARecordedHouse.Area("stue")],
			[
				ARecordedHouse.Heater("switch.stue", "stue") with { DeviceId = "plug" },
				Meter("sensor.plug_power", "stue", []) with { DeviceId = "plug" },
				ARecordedHouse.OurThermostat("climate.stue", "stue")
			]);

		HeatingConfig document = HeatingConfig.CreateDefault();
		document.Areas.Add(new AreaConfig { AreaId = "stue" });

		RoomRegulation sending = Assert.Single(RoomsFromTheAreas.Regulation(ACabin.WithOneRoom(), document, house.Resolver()));

		await using AnOpenConnection open = await AnOpenConnection.StartAsync();
		await open.RoomSettings.SendWhatChangedAsync([sending], CancellationToken.None);

		SentCall sent = Assert.Single(open.Core.Calls, call => call.Action == "adaptive_heating.set_regulation");

		Assert.Contains("\"power_sensor\":\"sensor.plug_power\"", sent.Json, StringComparison.Ordinal);
	}

	private static RecordedEntity Meter(string entityId, string areaId, IReadOnlyList<string> labels) =>
		new(entityId, areaId, labels, ARecordedHouse.Theirs) { DeviceClass = "power" };

	private static ResolvedRoom Resolve(IEnumerable<RecordedEntity> entities, AreaConfig? area = null)
	{
		ARecordedHouse house = new([ARecordedHouse.Area("stue")], entities);

		Assert.True(house.Resolver().TryResolve(area ?? new AreaConfig { AreaId = "stue" }, new AreaSettings(), out ResolvedRoom? room, out string? error), error);

		return room;
	}
}
