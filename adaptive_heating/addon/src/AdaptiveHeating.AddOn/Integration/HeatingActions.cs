namespace AdaptiveHeating.AddOn.Integration;

/// <summary>The five actions and their fields, named once on this side of the boundary.</summary>
/// <remarks>
///     The integration declares the same five in <c>core/actions.py</c>. The two copies drift, and a field
///     renamed on one side has to be renamed on the other by hand. A field the integration does not know is
///     dropped there rather than refused, and an action it does not have is absent rather than fatal.
/// </remarks>
internal static class HeatingActions
{
	public const string Domain = Adaptive.Engine.AreaEntityResolver.OurPlatform;

	/// <summary>Home Assistant's own climate domain, whose standard action carries the temperature to hold now.</summary>
	public const string ClimateDomain = "climate";

	public const string SetTemperature = "set_temperature";

	/// <summary>Home Assistant's own preset action, and the one field it takes.</summary>
	public const string SetPresetMode = "set_preset_mode";

	public const string PresetMode = "preset_mode";

	public const string WarmRoomBy = "warm_room_by";
	public const string HoldTemperature = "hold_temperature";
	public const string SetWarmingRate = "set_warming_rate";
	public const string SetRegulation = "set_regulation";
	public const string ReturnToTarget = "return_to_target";

	/// <summary>The room's thermostat placed in another area, keeping its entity, its history and its learnt numbers.</summary>
	public const string MoveToArea = "move_to_area";

	public const string Area = "area";

	/// <summary>The one house action, with no target: which label ids find the rooms.</summary>
	public const string SetLabels = "set_labels";
	public const string IncludeLabel = "include_label";
	public const string ExcludeLabel = "exclude_label";

	public const string Temperature = "temperature";
	public const string ByTime = "by_time";
	public const string Duration = "duration";
	public const string DegreesPerHour = "degrees_per_hour";
	public const string MeasuredAtOutdoor = "measured_at_outdoor";
	public const string Behaviour = "behaviour";
	public const string Band = "band";
	public const string OutdoorShiftPerDegree = "outdoor_shift_per_degree";
	public const string CycleLength = "cycle_length";
	public const string FullOutputBelow = "full_output_below";
	public const string ShortestTimeBetweenRelayChanges = "shortest_time_between_relay_changes";

	/// <summary>The per-room settings that travel on <see cref="SetRegulation"/> against the room's own thermostat.</summary>
	// A field the integration does not know is removed there rather than refused, so a value it has no slot for
	// costs nothing and reaches nothing.
	public const string SensorFreshness = "sensor_freshness";

	public const string StuckReading = "stuck_reading";
	public const string SensorSpread = "sensor_spread";
	public const string WindowDelay = "window_delay";
	public const string Heaters = "heaters";
	public const string TemperatureSensors = "temperature_sensors";
	public const string WindowSensor = "window_sensor";
	public const string PowerSensor = "power_sensor";
	public const string Neighbour = "neighbour";
	public const string OutdoorSensor = "outdoor_sensor";

	/// <summary>What a blind thermostat room's off-phase writes, and whether that limit holds at all.</summary>
	public const string FrostTemperature = "frost_temperature";

	public const string FrostLimit = "frost_limit";

	/// <summary>On or off for a room with no reading, in place of the temperature another room is given.</summary>
	public const string Switched = "switched";

	public const string SwitchedOn = "on";
	public const string SwitchedOff = "off";

	/// <summary>The attributes every one of this integration's thermostats carries, and nothing else does.</summary>
	public const string RoomIdAttribute = "room_id";
	public const string IntegrationVersionAttribute = "integration_version";

	/// <summary>The area the room is in now. Never a key: the room id is.</summary>
	public const string AreaIdAttribute = "area_id";

	/// <summary>What the add-on reads off a thermostat to drive it.</summary>
	public const string CurrentTemperatureAttribute = "current_temperature";
	public const string TargetTemperatureAttribute = "temperature";
	public const string HvacActionAttribute = "hvac_action";
	public const string NoReadingAtAllAttribute = "no_reading_at_all";
	public const string OutdoorAttribute = "outdoor";
	public const string TargetSetAtAttribute = "target_set_at";
	public const string WarmUpAttribute = "warm_up";
	public const string HoldEndsAtAttribute = "hold_ends_at";
	public const string PowerAttribute = "power";
	public const string HeaterCountAttribute = "heater_count";
	public const string HeatersNotRespondingAttribute = "heaters_not_responding";
	public const string HasAPowerMeterAttribute = "has_a_power_meter";
	public const string FaultAttribute = "fault";
	public const string ReportsAProblemAttribute = "reports_a_problem";

	/// <summary>The lowest a thermostat underneath the room accepts; absent where the room heats through a switch.</summary>
	public const string ThermostatMinimumAttribute = "thermostat_minimum";

	/// <summary>The room thermostat's own lower bound, below which Home Assistant refuses a temperature.</summary>
	public const string MinTempAttribute = "min_temp";

	/// <summary>The preset held on the room's card, and when it was chosen there.</summary>
	public const string PresetModeAttribute = "preset_mode";

	public const string PresetSetAtAttribute = "preset_set_at";

	/// <summary>The platform's feature flags, and the one that says a thermostat takes a preset.</summary>
	public const string SupportedFeaturesAttribute = "supported_features";

	public const int PresetModeFeature = 16;

	/// <summary>What <see cref="HvacActionAttribute"/> reads while the relay is closed.</summary>
	public const string Heating = "heating";
}
