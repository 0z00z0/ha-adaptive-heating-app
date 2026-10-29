using Adaptive.Engine;

using YamlDotNet.Serialization;

namespace Adaptive.Settings;

/// <summary>The one behaviour a house-mode option carries.</summary>
public enum ModeKind
{
	Normal,

	Sleep,

	Away,

	Boost,

	PlannedToArrive,

	OnTheWay,

	TemporarilyAway,

	/// <summary>Holds every room as it stands until a reset trigger fires; a change at a thermostat still counts.</summary>
	Guest
}

/// <summary>Which side owns the house mode: this application, or the Home Assistant dropdown.</summary>
/// <remarks>Ordinals are pinned and no member may be renamed or removed, for the reason <see cref="PeriodAuthority"/> gives.</remarks>
public enum HouseModeAuthority
{
	/// <summary>
	///     The default. A period's <c>SetsModeId</c>, the no-motion rule and <c>ActivateWhileOn</c> all write the mode,
	///     and the select is kept in step as a mirror.
	/// </summary>
	AdaptiveLighting = 0,

	/// <summary>
	///     Home Assistant decides. The engine reads the select and never writes it, and its own mode rules stand down:
	///     <see cref="TimePeriodConfig.SetsModeId"/> and <c>ActivateAfterNoMotionMinutes</c> stop firing.
	/// </summary>
	HomeAssistant = 1
}

/// <summary>The house-mode select and what each of its options means to the engine.</summary>
public class HouseModeConfig
{
	public string? Entity { get; set; }

	/// <summary><see cref="Entity"/> as Home Assistant should be asked for it: trimmed, or <c>null</c> when blank.</summary>
	/// <remarks>Same accessor and reason as <see cref="PeriodSelectConfig.EntityId"/>: an untrimmed value puts the engine on one entity and the pages on another.</remarks>
	[YamlIgnore]
	public string? EntityId => Entity is { Length: > 0 } entity && entity.Trim() is { Length: > 0 } trimmed
		? trimmed
		: null;

	/// <summary>Which side decides; the default <see cref="HouseModeAuthority.AdaptiveLighting"/> means adding the key changes nothing.</summary>
	public HouseModeAuthority Authority { get; set; } = HouseModeAuthority.AdaptiveLighting;

	/// <summary>Whether Home Assistant owns the mode, so nothing in the engine may write the select.</summary>
	[YamlIgnore]
	public bool HomeAssistantDecides => Authority is HouseModeAuthority.HomeAssistant && EntityId is not null;

	public List<HouseModeOptionConfig> Options { get; set; } = [];

	/// <summary>The configured option whose value equals <paramref name="value"/> (ordinal-insensitive, trimmed), or null.</summary>
	/// <remarks>
	///     <see cref="HouseModeOptionConfig.Value"/> is Home Assistant's string, so this is the match against what the
	///     select reports. References from inside this document go through <see cref="OptionWithKey"/>.
	/// </remarks>
	public HouseModeOptionConfig? OptionFor(string? value) =>
		value is { Length: > 0 }
			? Options.FirstOrDefault(o => o.Value.SameName(value))
			: null;

	/// <summary>The option a reference from inside this document names, or <c>null</c> when none does.</summary>
	public HouseModeOptionConfig? OptionWithKey(string? key) =>
		key is { Length: > 0 }
			? Options.FirstOrDefault(o => o.Key.SameName(key))
			: null;

	/// <summary>
	///     The select option string a reference resolves to: the configured row's <see cref="HouseModeOptionConfig.Value"/>,
	///     or the reference itself when nothing configured carries that key.
	/// </summary>
	/// <remarks>The fallback keeps a <c>SetsModeId</c> naming a live but unclassified option working.</remarks>
	public string? OptionValueFor(string? key) =>
		key is { Length: > 0 }
			? OptionWithKey(key)?.Value?.Trim() ?? key.Trim()
			: null;

	/// <summary>
	///     The single reset target: the first option marked <see cref="ModeKind.Normal"/>. It never falls back to a
	///     tagged option, so with nothing Normal every reset is a no-op.
	/// </summary>
	[YamlIgnore]
	public HouseModeOptionConfig? NormalOption =>
		Options.FirstOrDefault(o => o.Kind == ModeKind.Normal);

	/// <summary>Whether any option would put the house in away.</summary>
	[YamlIgnore]
	public bool HasAwayOption => Options.Any(o => o.Kind == ModeKind.Away);

	/// <summary>The upper limit while asleep: the option's clamp, else the period that sets it, else one named <c>night</c>.</summary>
	public static TimePeriodConfig? SleepClampPeriodFor(HouseModeOptionConfig option, IReadOnlyList<TimePeriodConfig> periods)
	{
		ArgumentNullException.ThrowIfNull(option);
		ArgumentNullException.ThrowIfNull(periods);

		if (option.ClampPeriodId is { Length: > 0 } clamp)
			return periods.ByKey(clamp);

		TimePeriodConfig? bySetsMode = periods.FirstOrDefault(p =>
			p.SetsModeId is { Length: > 0 } && p.SetsModeId.SameName(option.Key));
		if (bySetsMode is not null)
			return bySetsMode;

		return periods.ByName("night");
	}
}

/// <summary>One option value of the house-mode select, and the behaviour and reset triggers it carries.</summary>
public class HouseModeOptionConfig
{
	/// <summary>What a reference from inside this document names. Minted once, never shown, never changed.</summary>
	/// <remarks>Only this document's own references use it. Matching what Home Assistant reports is <see cref="Value"/>'s job.</remarks>
	public string? Id { get; set; }

	/// <summary>The exact option string as the select reports it. Compared case-insensitively, whitespace-trimmed.</summary>
	public string Value { get; set; } = "";

	/// <summary>What a reference from inside this document resolves by.</summary>
	// Falls back to Value for the binder that has no migration; see TimePeriodConfig.Key.
	[YamlIgnore]
	public string Key => Id is { Length: > 0 } id ? id.Trim() : Value.Trim();

	/// <summary>The option's one behaviour. One option should be <see cref="ModeKind.Normal"/>, the reset target.</summary>
	public ModeKind Kind { get; set; } = ModeKind.Normal;

	/// <summary>Sleep only: the <see cref="TimePeriodConfig.Id"/> every room is held at or below. Optional; see <see cref="HouseModeConfig.SleepClampPeriodFor"/>.</summary>
	public string? ClampPeriodId { get; set; }

	/// <summary>Sleep only: every room with its own setback switched on is set back while this option is in force.</summary>
	public bool NightSetback { get; set; }

	/// <summary>A <see cref="TimePeriodConfig.Id"/>. When that period starts, reset to Normal. Only meaningful on a non-Normal option.</summary>
	public string? ResetOnPeriodStartId { get; set; }

	public bool ResetOnPresence { get; set; }

	/// <summary>
	///     The presence sensors whose turn-on resets this mode. Empty means auto: the union of every motion sensor
	///     configured across all areas. Members are <c>binary_sensor.*</c> and/or <c>person.*</c>.
	/// </summary>
	public List<string> ResetPresenceSensors { get; set; } = [];

	/// <summary>Presence events within this many minutes of the mode being set are ignored, so leaving cannot cancel its own Away.</summary>
	public int ResetPresenceGraceMinutes { get; set; } = 15;

	/// <summary>
	///     Entities that force this option to be the effective house mode while any of them is on. Empty means the
	///     select alone decides. An overlay on the select, never written back to it, re-evaluated on any state change.
	/// </summary>
	public List<string> ActivateWhileOn { get; set; } = [];

	/// <summary>
	///     Switches the house to this option once the whole house has had no motion for this many minutes.
	///     <c>null</c> disables it. Watches the same motion union the presence reset defaults to.
	/// </summary>
	public int? ActivateAfterNoMotionMinutes { get; set; }

	/// <summary>Whether this option carries any reset trigger. Used by the normaliser to keep non-trivial rows.</summary>
	/// <remarks>Presence is gated on <see cref="ResetOnPresence"/> alone, so sensors listed with the toggle off are inert everywhere.</remarks>
	[YamlIgnore]
	public bool HasResetTrigger => ResetOnPeriodStartId is { Length: > 0 } || ResetOnPresence;
}
