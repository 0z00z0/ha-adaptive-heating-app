namespace Adaptive.UI;

/// <summary>
///     A Home Assistant dropdown's live options set against what the document stores for them: which are still
///     offered, which the helper has stopped offering, and which nothing maps yet.
/// </summary>
/// <remarks>
///     The house mode and the period select both reconcile a helper against stored rows. The shapes they render
///     differ, but the reconciliation is one answer and lives in one place.
/// </remarks>
public sealed class HelperOptions
{
	private readonly List<string> _live;
	private readonly string? _active;

	private HelperOptions(List<string> live, List<string> trailing, List<string> unmapped, string? active, bool answered)
	{
		_live = live;
		_active = active;

		// Rendered whether or not the helper answered, but called orphans only when it did: a stored row must not
		// vanish off the screen because the socket blinked, nor be badged as renamed.
		Display = [.. live, .. trailing];
		Orphans = answered ? trailing : [];
		Unmapped = unmapped;
	}

	/// <summary>The helper's own options, trimmed, de-duplicated, in the order Home Assistant reported them.</summary>
	public IReadOnlyList<string> Live => _live;

	/// <summary>Stored values the helper no longer offers: the rename case. Always empty when nothing was reported.</summary>
	public IReadOnlyList<string> Orphans { get; }

	/// <summary>Live options no stored row names.</summary>
	public IReadOnlyList<string> Unmapped { get; }

	/// <summary>What to render, in order: the live options, then the orphans behind them.</summary>
	public IReadOnlyList<string> Display { get; }

	/// <summary>Whether Home Assistant reported any options at all.</summary>
	public bool Answered => _live.Count > 0;

	/// <summary>Reconciles <paramref name="live"/> against <paramref name="stored"/>.</summary>
	/// <param name="live">What Home Assistant reports the helper offers. Empty means it has not answered.</param>
	/// <param name="stored">The values the document has rows for.</param>
	/// <param name="activeValue">The option the helper stands on now, if it is readable.</param>
	public static HelperOptions Reconcile(
		IReadOnlyList<string>? live,
		IEnumerable<string?>? stored,
		string? activeValue = null)
	{
		List<string> liveValues = Clean(live);
		List<string> storedValues = Clean(stored);

		List<string> trailing =
			[.. storedValues.Where(value => !liveValues.Any(offered => offered.SameName(value)))];

		List<string> unmapped =
			[.. liveValues.Where(value => !storedValues.Any(row => row.SameName(value)))];

		return new HelperOptions(liveValues, trailing, unmapped, activeValue?.Trim(), liveValues.Count > 0);
	}

	/// <summary>Whether the helper still offers <paramref name="value"/>. True for everything when it has not answered.</summary>
	public bool IsLive(string? value) =>
		_live.Count == 0 || _live.Any(offered => offered.SameName(value));

	/// <summary>Whether the helper is standing on <paramref name="value"/> right now.</summary>
	public bool IsActive(string? value) =>
		_active is { Length: > 0 } && _active.SameName(value);

	// HouseModeSync's, never a copy: blank-dropping and case-insensitive de-duplication are one answer, so both
	// screens agree that "Dag " and "dag" are one option.
	private static List<string> Clean(IEnumerable<string?>? values) => HouseModeSync.Clean(values ?? []);
}
