using YamlDotNet.Serialization;

namespace Adaptive.Settings;

/// <summary>One room. Every settings property is a nullable twin of <see cref="AreaSettings"/>: null inherits.</summary>
public class AreaConfig
{
	public string? Name { get; set; }

	/// <summary>HA registry area id (the slug), not the display name. Drives discovery and keys every setting.</summary>
	public string? AreaId { get; set; }

	// A hand-picked list is the slot's whole set: no label, no discovery and no exclusion re-filters it, and it
	// reaches the integration too. Null lets discovery find them; an empty list would not.
	public List<string>? Heaters { get; set; }

	public List<string>? TemperatureSensors { get; set; }

	public string? WindowSensor { get; set; }

	public string? PowerSensor { get; set; }

	/// <summary>The room this one takes its reading from, by area id, where it has no sensor of its own.</summary>
	public string? Neighbour { get; set; }

	// Paused rooms are heated as in Away; held rooms keep their temperature through a mode or period change.
	public List<string>? PauseWhenOn { get; set; }

	public bool? PauseWhenOnInverted { get; set; }

	public List<string>? HoldWhenOn { get; set; }

	public bool? HoldWhenOnInverted { get; set; }

	public List<string>? ExcludeEntities { get; set; }

	public bool? TreatAutomationsAsManual { get; set; }

	public double? AwayTemperature { get; set; }

	public double? BoostTemperature { get; set; }

	public bool? NightSetback { get; set; }

	/// <inheritdoc cref="AreaSettings.LowestNightTemperature"/>
	public double? LowestNightTemperature { get; set; }

	/// <inheritdoc cref="AreaSettings.FrostTemperature"/>
	public double? FrostTemperature { get; set; }

	/// <inheritdoc cref="AreaSettings.FrostProtection"/>
	public bool? FrostProtection { get; set; }

	/// <summary>What this room holds instead of the house value, period by period.</summary>
	public List<RoomPeriodTemperature> PeriodTemperatures { get; set; } = [];

	public int? OverrideDurationMinutes { get; set; }

	public bool? Enabled { get; set; }

	// A key into the document, never shown to a person: it cannot consult Home Assistant.
	[YamlIgnore]
	public string DisplayName => Name ?? AreaId ?? "(unnamed area)";

	/// <summary>Merges this room's overrides onto <paramref name="defaults"/>. Pure: neither argument is mutated.</summary>
	public AreaSettings Effective(AreaSettings defaults)
	{
		ArgumentNullException.ThrowIfNull(defaults);

		return new AreaSettings
		{
			OverrideDurationMinutes = OverrideDurationMinutes ?? defaults.OverrideDurationMinutes,
			LowestNightTemperature = LowestNightTemperature ?? defaults.LowestNightTemperature,
			FrostTemperature = FrostTemperature ?? defaults.FrostTemperature,
			FrostProtection = FrostProtection ?? defaults.FrostProtection,
			Enabled = Enabled ?? defaults.Enabled
		};
	}
}

/// <summary>One room's temperature for one period of the day, in place of the house value.</summary>
public class RoomPeriodTemperature
{
	public string? PeriodId { get; set; }

	public double? Temperature { get; set; }

	[YamlIgnore]
	public bool IsEmpty => Temperature is null;
}
