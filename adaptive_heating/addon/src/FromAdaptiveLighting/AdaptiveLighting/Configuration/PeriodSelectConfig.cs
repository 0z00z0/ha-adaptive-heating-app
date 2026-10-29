using YamlDotNet.Serialization;

namespace Adaptive.Settings;

/// <summary>Which side owns the time of day: this application's schedule, or a Home Assistant dropdown.</summary>
/// <remarks>
///     Ordinals are pinned and no member may be renamed or removed. An unknown enum value is a
///     <see cref="FormatException"/> at start-up, and <c>Enum.Parse</c> also accepts the bare numeral, so
///     <c>Authority: 1</c> in a hand-written file must keep meaning what it means now.
/// </remarks>
public enum PeriodAuthority
{
	/// <summary>The default: the engine resolves the period from its own schedule and writes the select as a mirror of that.</summary>
	AdaptiveLighting = 0,

	/// <summary>Home Assistant decides: the engine reads the select and never writes it, and the option it reports names the period every room runs.</summary>
	HomeAssistant = 1
}

/// <summary>A Home Assistant <c>input_select</c> tied to the period table, in whichever direction <see cref="Authority"/> names.</summary>
/// <remarks>
///     One entity, two jobs, never both at once. <see cref="Engine.PeriodSelectReader"/> holds one delegate per
///     direction and assigns one of them, so the split holds by construction and needs no check per use.
/// </remarks>
public class PeriodSelectConfig
{
	public string? Entity { get; set; }

	/// <summary><see cref="Entity"/> as Home Assistant should be asked for it: trimmed, or <c>null</c> when blank.</summary>
	/// <remarks>Every reader goes through here, never <see cref="Entity"/>: an untrimmed value resolves no period while the document still validates.</remarks>
	[YamlIgnore]
	public string? EntityId => Entity is { Length: > 0 } entity && entity.Trim() is { Length: > 0 } trimmed
		? trimmed
		: null;

	public PeriodAuthority Authority { get; set; } = PeriodAuthority.AdaptiveLighting;

	public List<PeriodSelectOptionConfig> Options { get; set; } = [];

	/// <summary>The period id <paramref name="value"/> stands for, or <c>null</c> when nothing maps it.</summary>
	/// <remarks>Trimmed and case-insensitive: the options are display strings a person typed into an HA helper.</remarks>
	public string? PeriodFor(string? value)
	{
		if (value is not { Length: > 0 })
			return null;

		// Trimmed once, outside the predicate: this runs per area per evaluation once a house follows the select.
		string needle = value.Trim();

		return Options
			.FirstOrDefault(option => string.Equals(option.Value?.Trim(), needle, StringComparison.OrdinalIgnoreCase))
			?.PeriodId?.Trim();
	}

	/// <summary>
	///     The select option standing for the period keyed <paramref name="periodId"/>, or <c>null</c> when no row
	///     names it. First row wins on a duplicate, as every other lookup in this document does.
	/// </summary>
	public string? OptionFor(string? periodId)
	{
		if (periodId is not { Length: > 0 })
			return null;

		string needle = periodId.Trim();

		return Options
			.FirstOrDefault(option => string.Equals(option.PeriodId?.Trim(), needle, StringComparison.OrdinalIgnoreCase))
			?.Value?.Trim();
	}
}

/// <summary>One select option, and the engine period it stands for.</summary>
public class PeriodSelectOptionConfig
{
	/// <summary>The exact option string as the select reports it. Compared case-insensitively, whitespace-trimmed.</summary>
	public string Value { get; set; } = "";

	/// <summary>The engine period this option means, by <see cref="TimePeriodConfig.Id"/>.</summary>
	/// <remarks>
	///     An id matching no period is an error here where the same shape is only a warning on a room's levels: an
	///     unresolvable mapping leaves the whole house unable to place the selected time of day.
	/// </remarks>
	public string PeriodId { get; set; } = "";

	/// <summary>Whether this row says nothing, so <see cref="ConfigNormalizer"/> can drop it on save.</summary>
	[YamlIgnore]
	public bool IsEmpty => string.IsNullOrWhiteSpace(Value) && string.IsNullOrWhiteSpace(PeriodId);
}
