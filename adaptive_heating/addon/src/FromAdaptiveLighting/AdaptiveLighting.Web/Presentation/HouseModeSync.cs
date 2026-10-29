using Adaptive.Settings;

namespace Adaptive.UI;

/// <summary>How the configured house-mode options stand against the options the dropdown helper offers.</summary>
/// <param name="CanCompare">
///     Whether the helper answered with options. <c>false</c> means the question cannot be settled, never that the
///     helper is empty: an unreachable Home Assistant answers with an empty list too, which read as a comparison
///     says every configured mode should be dropped.
/// </param>
/// <param name="Added">Option values the helper offers that the document has no entry for.</param>
/// <param name="Dropped">Option values the document carries that the helper no longer offers.</param>
public sealed record HouseModeOptionsDiff(
	bool CanCompare,
	IReadOnlyList<string> Added,
	IReadOnlyList<string> Dropped)
{
	/// <summary>Nothing to do: the two lists name the same options.</summary>
	public bool Matches => CanCompare && Added.Count == 0 && Dropped.Count == 0;

	/// <summary>The two lists name different options.</summary>
	public bool Differs => CanCompare && (Added.Count > 0 || Dropped.Count > 0);
}

/// <summary>Compares the document's house-mode options against the helper's, and words how the two have drifted.</summary>
/// <remarks>Reports only. Nothing writes the option list from a helper.</remarks>
public static class HouseModeSync
{
	/// <summary>What the helper offers against what the document carries.</summary>
	/// <param name="mode">The configured house mode, or <c>null</c> when the document has none.</param>
	/// <param name="liveOptions">The helper's options as Home Assistant last reported them. Empty means "cannot tell".</param>
	public static HouseModeOptionsDiff Compare(HouseModeConfig? mode, IReadOnlyList<string> liveOptions)
	{
		ArgumentNullException.ThrowIfNull(liveOptions);

		List<string> live = Clean(liveOptions);

		// An empty live list cannot be told from a silent Home Assistant, so it is never a difference to act on.
		if (mode?.Entity is not { Length: > 0 } || live.Count == 0)
			return new HouseModeOptionsDiff(false, [], []);

		List<string> configured = Clean(mode.Options.Select(option => option.Value));

		// Sets, trimmed and case-insensitive, matching HouseModeConfig.OptionFor. Order is not part of it.
		return new HouseModeOptionsDiff(
			true,
			[.. live.Where(value => !configured.Contains(value, StringComparer.OrdinalIgnoreCase))],
			[.. configured.Where(value => !live.Contains(value, StringComparer.OrdinalIgnoreCase))]);
	}

	/// <summary>Which way the two lists differ, in one sentence, or <c>null</c> when they do not.</summary>
	/// <remarks>Describes, never proposes: there is no control to press.</remarks>
	public static string? Drift(HouseModeOptionsDiff diff)
	{
		ArgumentNullException.ThrowIfNull(diff);

		if (!diff.Differs)
			return null;

		// Join reads parts[0] and parts[^1], so neither list may be empty when it is called.
		if (diff.Dropped.Count == 0)
			return $"It offers {Join(diff.Added)}, which nothing here describes yet.";

		string stranded = $"It no longer offers {Join(diff.Dropped)}, which stays below so you can move it across or remove it.";

		return diff.Added.Count == 0
			? stranded
			: $"It offers {Join(diff.Added)}, which nothing here describes yet. {stranded}";
	}

	/// <summary>Trimmed, non-blank and named once each: the shape both sides of the comparison are read in.</summary>
	public static List<string> Clean(IEnumerable<string?> values) =>
	[
		.. values
			.Where(value => !string.IsNullOrWhiteSpace(value))
			.Select(value => value!.Trim())
			.Distinct(StringComparer.OrdinalIgnoreCase)
	];

	/// <summary>"a", "a and b", "a, b and c".</summary>
	private static string Join(IReadOnlyList<string> parts) => parts.Count switch
	{
		1 => parts[0],
		2 => $"{parts[0]} and {parts[1]}",
		_ => $"{string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]}"
	};
}
