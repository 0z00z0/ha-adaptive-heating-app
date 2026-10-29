namespace Adaptive.Engine;

/// <summary>Comparing the names a person types: helper options, period keys, mode values, device classes.</summary>
/// <remarks>Trimmed and case-insensitive, because each of them reaches the engine through a hand-filled field.</remarks>
public static class NameExtensions
{
	/// <summary>Whether two names are the same once trimmed, ordinal-ignore-case.</summary>
	/// <remarks>
	///     Two nulls match and null matches nothing else, and <c>""</c> is not <c>null</c>, so a caller that wants
	///     them folded together drops the blank before asking.
	/// </remarks>
	public static bool SameName(this string? name, string? other) =>
		string.Equals(name?.Trim(), other?.Trim(), StringComparison.OrdinalIgnoreCase);
}
