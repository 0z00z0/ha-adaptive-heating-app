using System.Text.Json.Serialization;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Adaptive.Engine;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     The wire shape of an <c>adaptive_heating_area</c> event, as
///     <see cref="AdaptiveLighting.Ha.HaStatePublisher"/> writes it.
/// </summary>
// NetDaemon's Event<T>.Data deserializes case-sensitively with no options, so every name is the publisher's own.
public sealed record AreaSnapshotEvent
{
	[JsonPropertyName("area")]
	public string? Area { get; init; }

	[JsonPropertyName("area_id")]
	public string? AreaId { get; init; }

	/// <summary>The <see cref="AreaState"/> name.</summary>
	[JsonPropertyName("state")]
	public string? State { get; init; }

	/// <summary>The <see cref="TransitionReason"/> name.</summary>
	[JsonPropertyName("reason")]
	public string? Reason { get; init; }

	/// <summary>The house mode's published word, which is "Home" for the everyday kind.</summary>
	[JsonPropertyName("mode")]
	public string? Mode { get; init; }

	[JsonPropertyName("kill_switch_active")]
	public bool KillSwitchActive { get; init; }

	[JsonPropertyName("period")]
	public string? Period { get; init; }

	[JsonPropertyName("target")]
	public double? Target { get; init; }

	[JsonPropertyName("temperature")]
	public double? Temperature { get; init; }

	[JsonPropertyName("timestamp")]
	public DateTimeOffset Timestamp { get; init; }

	[JsonPropertyName("last_command_at")]
	public DateTimeOffset? LastCommandAt { get; init; }

	[JsonPropertyName("next_change_at")]
	public DateTimeOffset? NextChangeAt { get; init; }

	[JsonPropertyName("next_change_from")]
	public DateTimeOffset? NextChangeFrom { get; init; }

	[JsonPropertyName("house_mode_value")]
	public string? HouseModeValue { get; init; }

	/// <summary>An <see cref="AutoOnBlock"/> name; <c>null</c> means this report cannot say.</summary>
	[JsonPropertyName("auto_on_blocked_by")]
	public string? AutoOnBlockedBy { get; init; }

	[JsonPropertyName("auto_on_blocking_entity")]
	public string? AutoOnBlockingEntity { get; init; }

	[JsonPropertyName("is_held")]
	public bool? IsHeld { get; init; }

	[JsonPropertyName("held_by")]
	public string? HeldBy { get; init; }

	[JsonPropertyName("target_from")]
	public string? TargetFrom { get; init; }

	[JsonPropertyName("heaters_not_responding")]
	public int? HeatersNotResponding { get; init; }

	[JsonPropertyName("heater_count")]
	public int? HeaterCount { get; init; }

	[JsonPropertyName("temperature_sensors_low_battery")]
	public List<SensorBatteryEvent>? TemperatureSensorsLowBattery { get; init; }

	[JsonPropertyName("changed_by")]
	public string? ChangedBy { get; init; }

	[JsonPropertyName("changed_at")]
	public DateTimeOffset? ChangedAt { get; init; }

	[JsonPropertyName("levels_from_room")]
	public bool? LevelsFromRoom { get; init; }

	[JsonPropertyName("is_anyone_home")]
	public bool? IsAnyoneHome { get; init; }

	[JsonPropertyName("mode_forced_kind")]
	public string? ModeForcedKind { get; init; }

	[JsonPropertyName("mode_forced_option")]
	public string? ModeForcedOption { get; init; }

	[JsonPropertyName("mode_forced_source")]
	public string? ModeForcedSource { get; init; }

	[JsonPropertyName("mode_forced_by")]
	public string? ModeForcedBy { get; init; }

	[JsonPropertyName("mode_forced_by_state")]
	public string? ModeForcedByState { get; init; }

	[JsonPropertyName("active_mode_name")]
	public string? ActiveModeName { get; init; }

	/// <summary>The snapshot again, or <c>null</c> when the payload names no area. A bad enum name degrades; nothing throws.</summary>
	public AreaSnapshot? ToSnapshot()
	{
		if (string.IsNullOrWhiteSpace(Area))
			return null;

		return new AreaSnapshot(
			Area,
			Enum.TryParse<AreaState>(State, out AreaState state) ? state : default,
			Enum.TryParse<TransitionReason>(Reason, out TransitionReason reason) ? reason : default,
			HouseModeName.Parse(Mode),
			KillSwitchActive,
			Period,
			Target,
			Temperature,
			Timestamp,
			LastCommandAt,
			NextChangeAt,
			NextChangeFrom,
			HouseModeValue,
			AreaId,
			// Not default: AutoOnBlock's zero is "nothing is blocking", which a report without the field did not say.
			Enum.TryParse(AutoOnBlockedBy, out AutoOnBlock block) ? block : null,
			AutoOnBlockingEntity,
			LevelsFromRoom,
			IsAnyoneHome,
			Enum.TryParse(ModeForcedKind, out ModeKind kind) && Enum.TryParse(ModeForcedSource, out ModeForceSource source)
				? new ForcedMode(kind, ModeForcedOption ?? "", source, ModeForcedBy, ModeForcedByState)
				: null,
			IsHeld,
			HeldBy,
			TargetFrom,
			HeatersNotResponding,
			HeaterCount,
			ChangedBy,
			ChangedAt,
			TemperatureSensorsLowBattery is { Count: > 0 } low
				? [.. low.Where(battery => battery.EntityId is { Length: > 0 }).Select(battery => new SensorBattery(battery.EntityId!, battery.LevelPct))]
				: null,
			ActiveModeName);
	}
}

/// <summary>One entry of <see cref="AreaSnapshotEvent.TemperatureSensorsLowBattery"/>, in the publisher's own names.</summary>
public sealed record SensorBatteryEvent
{
	[JsonPropertyName("entity_id")]
	public string? EntityId { get; init; }

	[JsonPropertyName("level_pct")]
	public double? LevelPct { get; init; }
}
