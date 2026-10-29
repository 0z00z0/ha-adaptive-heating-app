namespace Adaptive.Settings;

/// <summary>What the validator is told about the world outside the document.</summary>
/// <remarks>
///     A <c>null</c> collection means Home Assistant could not be asked, and the checks that need it are skipped.
///     It never means "nothing exists"; a set that is genuinely empty is an empty collection.
/// </remarks>
public sealed record ValidationContext
{
	/// <summary>Nothing known: every referential check is skipped, and no default switch applies.</summary>
	public static ValidationContext None { get; } = new();

	public IReadOnlyCollection<string>? KnownEntityIds { get; init; }

	public IReadOnlyCollection<string>? KnownAreaIds { get; init; }

	/// <summary>The house-mode select's live options.</summary>
	// Kept apart from LivePeriodSelectOptions: crossing two different helpers would report renames that never happened.
	public IReadOnlyCollection<string>? LiveSelectOptions { get; init; }

	/// <summary>Labels at least one entity carries, by id and by name, matching either way as the resolver does.</summary>
	public IReadOnlyCollection<string>? LabelsInUse { get; init; }

	public IReadOnlyCollection<string>? LivePeriodSelectOptions { get; init; }

	/// <summary>Every label id the house has, carried or not. Only an id set can tell a stored id from a stored name.</summary>
	public IReadOnlyCollection<string>? KnownLabelIds { get; init; }

	/// <summary>The add-on's built-in enable switch, the gate when the document names none.</summary>
	public string? DefaultGateEntity { get; init; }

	/// <summary>The entities this integration published, which are rooms and never a room's heaters.</summary>
	public IReadOnlyCollection<string>? OurOwnEntityIds { get; init; }
}
