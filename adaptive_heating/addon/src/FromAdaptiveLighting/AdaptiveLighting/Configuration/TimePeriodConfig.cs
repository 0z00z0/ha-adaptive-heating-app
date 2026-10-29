using YamlDotNet.Serialization;

namespace Adaptive.Settings;

/// <summary>One entry in the circadian table: the temperature the house holds from <see cref="Start"/> until the next period.</summary>
public class TimePeriodConfig
{
	/// <summary>What every reference to this period names. Minted once, never shown, never changed.</summary>
	/// <remarks>Filled in on load by <see cref="StableKeyMigration"/>. Editing it by hand orphans every reference to the old value.</remarks>
	public string? Id { get; set; }

	/// <summary>Free-form name: <c>morning</c>, <c>day</c>, <c>evening</c>, <c>night</c>. Reported in logs and snapshots. Two periods may share one.</summary>
	public string Name { get; set; } = "";

	/// <summary>What this period is resolved by, everywhere.</summary>
	// Falls back to Name for the one reader that can never be migrated: NetDaemon's ConfigurationBinder, against a
	// house's app YAML, has no pre-pass and so no ids.
	[YamlIgnore]
	public string Key => Id is { Length: > 0 } id ? id.Trim() : Name.Trim();

	/// <summary>
	///     The <see cref="HouseModeOptionConfig.Id"/> the house mode is set to when this period starts; <c>null</c>
	///     leaves the mode unchanged.
	/// </summary>
	/// <remarks>
	///     A value matching no configured option is written to the select verbatim, which is how a live option that
	///     nobody has classified yet still works.
	/// </remarks>
	public string? SetsModeId { get; set; }

	/// <summary>
	///     When this period begins: a clock time (<c>06:30</c>) or a sun event with an optional offset
	///     (<c>sunrise</c>, <c>sunset-01:00</c>). See <see cref="Adaptive.Settings.PeriodStart.TryParse"/>.
	/// </summary>
	public string Start { get; set; } = "";

	/// <summary>The temperature this period holds, house-wide, in degrees Celsius.</summary>
	public double? Temperature { get; set; }
}
