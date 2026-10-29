using Adaptive.Engine;
using Adaptive.Settings;
using Adaptive.UI;

using AdaptiveHeating.AddOn.Integration;
using AdaptiveHeating.Page.Settings;
using AdaptiveHeating.Planner;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>What travels to a room, and the order the rooms are shown in.</summary>
public sealed class EachRoomsOwnSettingsTravelToItTests
{
	private const string OutdoorSensor = "sensor.outdoor_north";

	[Fact]
	public async Task A_hand_picked_heater_leaves_on_the_regulation_action()
	{
		ARecordedHouse house = new(
			[ARecordedHouse.Area("stue")],
			[
				ARecordedHouse.Heater("switch.stue", "stue"),
				new RecordedEntity("switch.oven", "stue", [], ARecordedHouse.Theirs),
				ARecordedHouse.OurThermostat("climate.stue", "stue")
			]);

		HeatingConfig document = HeatingConfig.CreateDefault();
		document.Global.OutdoorSensor = OutdoorSensor;
		document.Areas.Add(new AreaConfig { AreaId = "stue", Heaters = ["switch.oven"] });

		IReadOnlyList<RoomRegulation> sending =
			RoomsFromTheAreas.Regulation(ACabin.WithOneRoom(), document, house.Resolver());

		RoomRegulation room = Assert.Single(sending);

		Assert.Equal("climate.stue", room.Thermostat);
		Assert.Equal(["switch.oven"], room.Heaters);
		Assert.Equal(OutdoorSensor, room.OutdoorSensor);

		await using AnOpenConnection open = await AnOpenConnection.StartAsync();

		Assert.Equal(["climate.stue"], await open.RoomSettings.SendWhatChangedAsync(sending, CancellationToken.None));

		SentCall sent = Assert.Single(open.Core.Calls, call => call.Action == "adaptive_heating.set_regulation");

		Assert.Contains("switch.oven", sent.Json, StringComparison.Ordinal);
		Assert.Contains("climate.stue", sent.Json, StringComparison.Ordinal);
		Assert.Contains(OutdoorSensor, sent.Json, StringComparison.Ordinal);

		// No room is named: the room is the target, so the key never travels.
		Assert.DoesNotContain("\"room\"", sent.Json, StringComparison.Ordinal);
	}

	[Fact]
	public async Task The_House_tabs_label_reaches_the_integration_as_its_id()
	{
		GlobalConfig global = new() { IncludeLabel = "varmekabel" };

		await using AnOpenConnection open = await AnOpenConnection.StartAsync();

		Assert.True(await open.RoomSettings.SendLabelsAsync(global, CancellationToken.None));
		Assert.False(await open.RoomSettings.SendLabelsAsync(global, CancellationToken.None));

		SentCall sent = Assert.Single(open.Core.Calls, call => call.Action == "adaptive_heating.set_labels");

		Assert.Contains("\"include_label\":\"varmekabel\"", sent.Json, StringComparison.Ordinal);
		Assert.Contains("\"exclude_label\":\"adaptive_exclude\"", sent.Json, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Settings_that_have_not_moved_are_not_sent_again()
	{
		ARecordedHouse house = new(
			[ARecordedHouse.Area("stue")],
			[ARecordedHouse.Heater("switch.stue", "stue"), ARecordedHouse.OurThermostat("climate.stue", "stue")]);

		HeatingConfig document = HeatingConfig.CreateDefault();
		document.Areas.Add(new AreaConfig { AreaId = "stue" });

		IReadOnlyList<RoomRegulation> sending =
			RoomsFromTheAreas.Regulation(ACabin.WithOneRoom(), document, house.Resolver());

		await using AnOpenConnection open = await AnOpenConnection.StartAsync();

		Assert.Single(await open.RoomSettings.SendWhatChangedAsync(sending, CancellationToken.None));
		Assert.Empty(await open.RoomSettings.SendWhatChangedAsync(sending, CancellationToken.None));
	}

	[Fact]
	public void A_room_discovery_refuses_keeps_its_name_and_loses_its_thermostat()
	{
		ARecordedHouse house = new([ARecordedHouse.Area("loft")], [ARecordedHouse.OurThermostat("climate.loft", "loft")]);

		HeatingConfig document = HeatingConfig.CreateDefault();
		document.Areas.Add(new AreaConfig { AreaId = "loft", Name = "Loft" });

		MergedRooms merged = RoomsFromTheAreas.Merge(
			HeatingDocument.NothingHeardYet(), document, new Dictionary<string, string> { ["loft"] = "room_001" }, house, house.Resolver(),
			PlannerDefaults.Standard);

		RoomSlice room = Assert.Single(merged.Document.Rooms);

		Assert.Equal("room_001", room.Id);
		Assert.Equal("Loft", room.Name);
		Assert.Null(room.Thermostat);
	}

	[Fact]
	public void A_room_is_keyed_by_its_room_id_and_named_by_the_registry()
	{
		ARecordedHouse house = new(
			[new RecordedArea("stue", []) { Name = "Living room" }],
			[ARecordedHouse.Heater("switch.stue", "stue"), ARecordedHouse.Sensor("sensor.stue", "stue")]);

		HeatingConfig document = HeatingConfig.CreateDefault();
		document.Areas.Add(new AreaConfig { AreaId = "stue" });

		MergedRooms merged = RoomsFromTheAreas.Merge(
			HeatingDocument.NothingHeardYet(), document, new Dictionary<string, string> { ["stue"] = "room_001" }, house, house.Resolver(),
			PlannerDefaults.Standard);

		RoomSlice room = Assert.Single(merged.Document.Rooms);

		Assert.Equal(("room_001", "stue"), (room.Id, room.AreaId));
		Assert.Equal("Living room", room.Name);
		Assert.False(room.NoReadingAtAll);
	}

	[Fact]
	public async Task The_room_s_frost_temperature_travels_and_no_limit_travels_as_off()
	{
		ARecordedHouse house = new(
			[ARecordedHouse.Area("stue"), ARecordedHouse.Area("bad")],
			[
				ARecordedHouse.Heater("switch.stue", "stue"),
				ARecordedHouse.OurThermostat("climate.stue", "stue"),
				ARecordedHouse.Heater("switch.bad", "bad"),
				ARecordedHouse.OurThermostat("climate.bad", "bad")
			]);

		HeatingConfig document = HeatingConfig.CreateDefault();
		document.Defaults.FrostTemperature = 6.5;
		document.Areas.Add(new AreaConfig { AreaId = "stue" });
		document.Areas.Add(new AreaConfig { AreaId = "bad", FrostProtection = false });

		IReadOnlyList<RoomRegulation> sending =
			RoomsFromTheAreas.Regulation(ACabin.WithOneRoom(), document, house.Resolver());

		await using AnOpenConnection open = await AnOpenConnection.StartAsync();

		await open.RoomSettings.SendWhatChangedAsync(sending, CancellationToken.None);

		string stue = Assert.Single(open.Core.Calls, call => call.Json.Contains("climate.stue", StringComparison.Ordinal)).Json;
		string bad = Assert.Single(open.Core.Calls, call => call.Json.Contains("climate.bad", StringComparison.Ordinal)).Json;

		Assert.Contains("\"frost_temperature\":6.5", stue, StringComparison.Ordinal);
		Assert.Contains("\"frost_limit\":\"on\"", stue, StringComparison.Ordinal);
		Assert.Contains("\"frost_limit\":\"off\"", bad, StringComparison.Ordinal);
		Assert.DoesNotContain("frost_temperature", bad, StringComparison.Ordinal);
	}

	[Fact]
	public void Two_floors_group_their_rooms_in_the_registrys_order()
	{
		AreaFloor ground = new("ground", "Ground floor", 0);
		AreaFloor upstairs = new("upstairs", "Upstairs", 1);

		ARecordedHouse house = new(
			[
				new RecordedArea("soverom", []) { Floor = upstairs },
				new RecordedArea("stue", []) { Floor = ground },
				new RecordedArea("bad", []) { Floor = upstairs },
				new RecordedArea("kjeller", [])
			],
			[]);

		IReadOnlyList<FloorGroup<string>> grouped =
			FloorGrouping.GroupOrFlat(["soverom", "stue", "bad", "kjeller"], areaId => areaId, house);

		Assert.Equal(3, grouped.Count);

		Assert.Equal("ground", grouped[0].Floor?.Id);
		Assert.Equal(["stue"], grouped[0].Items);

		Assert.Equal("upstairs", grouped[1].Floor?.Id);
		Assert.Equal(["soverom", "bad"], grouped[1].Items);

		// The floorless group trails them all, and carries no floor to put a header on.
		Assert.Null(grouped[2].Floor);
		Assert.Equal(["kjeller"], grouped[2].Items);
	}
}
