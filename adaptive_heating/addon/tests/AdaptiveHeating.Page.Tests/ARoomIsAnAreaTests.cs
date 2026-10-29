using Adaptive.Engine;
using Adaptive.Settings;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>Which Areas become rooms, and every gate that keeps one out.</summary>
public sealed class ARoomIsAnAreaTests
{
	[Fact]
	public void Two_Areas_with_a_labelled_heater_resolve_two_rooms()
	{
		ARecordedHouse house = new(
			[ARecordedHouse.Area("stue"), ARecordedHouse.Area("bad")],
			[ARecordedHouse.Heater("switch.stue", "stue"), ARecordedHouse.Heater("climate.bad", "bad")]);

		AreaEntityResolver resolver = house.Resolver();

		Assert.Equal(["switch.stue"], resolver.DiscoverArea("stue").Heaters);
		Assert.Equal(["climate.bad"], resolver.DiscoverArea("bad").Heaters);
	}

	[Fact]
	public void An_Area_holding_only_this_integrations_own_thermostat_is_no_room()
	{
		ARecordedHouse house = new(
			[ARecordedHouse.Area("loft")],
			[ARecordedHouse.OurThermostat("climate.loft", "loft")]);

		Assert.Empty(house.Resolver().DiscoverArea("loft").Heaters);
		Assert.Contains("own thermostats", house.Resolver().ExplainWhyNotARoom("loft"), StringComparison.Ordinal);
	}

	[Fact]
	public void A_room_naming_its_own_thermostat_as_a_heater_is_refused()
	{
		HeatingConfig document = HeatingConfig.CreateDefault();
		document.Areas.Add(new AreaConfig { AreaId = "loft", Heaters = ["climate.loft"] });

		ValidationResult refused = ConfigValidator.Validate(document, new ValidationContext
		{
			KnownEntityIds = ["climate.loft"],
			KnownAreaIds = ["loft"],
			OurOwnEntityIds = ["climate.loft"]
		});

		Assert.Contains(
			refused.AreaErrors,
			error => error.Message.Contains("own thermostat", StringComparison.Ordinal));
	}

	[Fact]
	public void A_hidden_labelled_heater_is_not_a_candidate()
	{
		ARecordedHouse house = new(
			[ARecordedHouse.Area("stue")],
			[ARecordedHouse.Heater("switch.stue", "stue") with { Hidden = true }]);

		Assert.Empty(house.Resolver().DiscoverArea("stue").Heaters);
	}

	[Fact]
	public void An_unlabelled_heater_is_not_a_candidate_while_the_label_is_required()
	{
		ARecordedHouse house = new(
			[ARecordedHouse.Area("stue")],
			[new RecordedEntity("switch.stue", "stue", [], ARecordedHouse.Theirs)]);

		Assert.Empty(house.Resolver().DiscoverArea("stue").Heaters);
		Assert.Contains(ARecordedHouse.Heating, house.Resolver().ExplainWhyNotARoom("stue"), StringComparison.Ordinal);
	}

	[Fact]
	public void A_heater_carrying_the_exclude_label_is_not_a_candidate()
	{
		ARecordedHouse house = new(
			[ARecordedHouse.Area("stue")],
			[
				ARecordedHouse.Heater("switch.stue", "stue"),
				new RecordedEntity("switch.panel", "stue", [ARecordedHouse.Heating, ARecordedHouse.Excluded], ARecordedHouse.Theirs)
			]);

		Assert.Equal(["switch.stue"], house.Resolver().DiscoverArea("stue").Heaters);
	}

	[Fact]
	public void An_Area_carrying_the_exclude_label_is_skipped_with_the_reason_naming_that_label()
	{
		ARecordedHouse house = new(
			[ARecordedHouse.Area("stue", ARecordedHouse.Excluded)],
			[ARecordedHouse.Heater("switch.stue", "stue")]);

		Assert.Empty(house.Resolver().DiscoverArea("stue").Heaters);
		Assert.Contains(ARecordedHouse.Excluded, house.Resolver().ExplainWhyNotARoom("stue"), StringComparison.Ordinal);
	}

	[Fact]
	public void An_Area_whose_only_heater_is_unavailable_is_skipped_with_the_reason_naming_availability()
	{
		ARecordedHouse house = new(
			[ARecordedHouse.Area("stue")],
			[ARecordedHouse.Heater("switch.stue", "stue") with { Available = false }]);

		Assert.Empty(house.Resolver().DiscoverArea("stue").Heaters);
		Assert.Contains("unavailable", house.Resolver().ExplainWhyNotARoom("stue"), StringComparison.Ordinal);
	}

	[Fact]
	public void Two_heater_entities_on_one_device_resolve_to_one()
	{
		ARecordedHouse house = new(
			[ARecordedHouse.Area("stue")],
			[
				ARecordedHouse.Heater("switch.stue_relay", "stue") with { DeviceId = "panel" },
				ARecordedHouse.Heater("switch.stue_channel_two", "stue") with { DeviceId = "panel" }
			]);

		Assert.Equal(["switch.stue_channel_two"], house.Resolver().DiscoverArea("stue").Heaters);
	}

	[Fact]
	public void A_heater_in_the_rooms_own_exclude_list_is_dropped_from_discovery()
	{
		ARecordedHouse house = new(
			[ARecordedHouse.Area("stue")],
			[ARecordedHouse.Heater("switch.stue", "stue"), ARecordedHouse.Heater("switch.fridge", "stue")]);

		AreaConfig room = new() { AreaId = "stue", ExcludeEntities = ["switch.fridge"] };

		Assert.True(house.Resolver().TryResolve(room, new AreaSettings(), out ResolvedRoom? resolved, out _));
		Assert.Equal(["switch.stue"], resolved.Heaters);
	}

	[Fact]
	public void A_room_naming_one_unlabelled_heater_resolves_to_that_heater_alone()
	{
		ARecordedHouse house = new(
			[ARecordedHouse.Area("stue")],
			[
				ARecordedHouse.Heater("switch.stue", "stue"),
				new RecordedEntity("switch.oven", "stue", [], ARecordedHouse.Theirs)
			]);

		// A hand-picked list is the slot's whole set: the labelled heater discovery would find is not added to it.
		AreaConfig room = new() { AreaId = "stue", Heaters = ["switch.oven"] };

		Assert.True(house.Resolver().TryResolve(room, new AreaSettings(), out ResolvedRoom? resolved, out _));
		Assert.Equal(["switch.oven"], resolved.Heaters);
	}

	[Fact]
	public void A_rooms_own_climate_entity_is_the_thermostat_every_action_targets()
	{
		ARecordedHouse house = new(
			[ARecordedHouse.Area("stue")],
			[ARecordedHouse.Heater("switch.stue", "stue"), ARecordedHouse.OurThermostat("climate.stue", "stue")]);

		AreaConfig room = new() { AreaId = "stue" };

		Assert.True(house.Resolver().TryResolve(room, new AreaSettings(), out ResolvedRoom? resolved, out _));
		Assert.Equal("climate.stue", resolved.Thermostat);
		Assert.Equal(["switch.stue"], resolved.Heaters);
	}

	[Fact]
	public void A_temperature_sensor_needs_the_device_class_and_the_label()
	{
		ARecordedHouse house = new(
			[ARecordedHouse.Area("stue")],
			[
				ARecordedHouse.Heater("switch.stue", "stue"),
				ARecordedHouse.Sensor("sensor.stue_temperature", "stue"),
				new RecordedEntity("sensor.stue_humidity", "stue", [ARecordedHouse.Heating], ARecordedHouse.Theirs) { DeviceClass = "humidity" },
				new RecordedEntity("sensor.stue_spare", "stue", [], ARecordedHouse.Theirs) { DeviceClass = "temperature" }
			]);

		Assert.Equal(["sensor.stue_temperature"], house.Resolver().DiscoverArea("stue").TemperatureSensors);
	}

	[Fact]
	public void An_Area_Home_Assistant_no_longer_has_says_so()
	{
		ARecordedHouse house = new([], []);

		Assert.Contains("no longer exists", house.Resolver().ExplainWhyNotARoom("gone"), StringComparison.Ordinal);
	}
}
