namespace Adaptive.Engine;

/// <summary>A Home Assistant label in both its forms.</summary>
/// <remarks>
///     Kept apart, because a match has to say which form hit: Home Assistant keeps the id through a rename, and a
///     new label may reuse an old name. <c>Name</c> falls back to the id when the label has none.
/// </remarks>
public sealed record RegistryLabel(string Id, string Name);

/// <summary>Whether a stored label value matches a label Home Assistant reports.</summary>
/// <remarks>
///     The one place the rule lives. A value the house knows as a label id matches that id and nothing else; any
///     other value still matches by name, so a document written before ids were stored keeps working and a start
///     with Home Assistant unreachable changes nothing.
/// </remarks>
public static class LabelMatch
{
	/// <summary>Whether <paramref name="carried"/> includes <paramref name="stored"/>.</summary>
	public static bool Carries(IReadOnlyList<RegistryLabel>? carried, IReadOnlyList<RegistryLabel>? knownLabels, string? stored)
	{
		if (carried is null || stored is not { Length: > 0 })
			return false;

		foreach (RegistryLabel label in carried)
			if (string.Equals(label.Id, stored, StringComparison.OrdinalIgnoreCase))
				return true;

		// Id-only from here on. Without this a renamed label and a new label reusing its name would both count.
		if (IsKnownId(knownLabels, stored))
			return false;

		foreach (RegistryLabel label in carried)
			if (string.Equals(label.Name, stored, StringComparison.OrdinalIgnoreCase))
				return true;

		return false;
	}

	/// <summary>Whether the house knows <paramref name="stored"/> as a label id.</summary>
	public static bool IsKnownId(IReadOnlyList<RegistryLabel>? knownLabels, string? stored)
	{
		if (knownLabels is null || stored is not { Length: > 0 })
			return false;

		foreach (RegistryLabel label in knownLabels)
			if (string.Equals(label.Id, stored, StringComparison.OrdinalIgnoreCase))
				return true;

		return false;
	}

	/// <summary>The id of the label <paramref name="stored"/> names, or <c>null</c> when nothing is named that.</summary>
	/// <remarks>Null for a value that is already an id, so a caller can tell "nothing to do" from "cannot translate".</remarks>
	public static string? IdForName(IReadOnlyList<RegistryLabel>? knownLabels, string? stored)
	{
		if (knownLabels is null || stored is not { Length: > 0 } || IsKnownId(knownLabels, stored))
			return null;

		foreach (RegistryLabel label in knownLabels)
			if (string.Equals(label.Name, stored, StringComparison.OrdinalIgnoreCase))
				return label.Id;

		return null;
	}

	/// <summary>The name to show for <paramref name="stored"/>, falling back to the stored value itself.</summary>
	public static string DisplayName(IReadOnlyList<RegistryLabel>? knownLabels, string stored)
	{
		if (knownLabels is not null)
			foreach (RegistryLabel label in knownLabels)
				if (string.Equals(label.Id, stored, StringComparison.OrdinalIgnoreCase))
					return label.Name;

		return stored;
	}
}
