using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdaptiveLighting.Engine;

/// <summary>A room that is switched on but could not be set up, and what was wrong with it.</summary>
/// <remarks><c>Key</c> identifies the room across restarts; <c>Area</c> is what a person is shown.</remarks>
public sealed record AreaSetupFault(string Key, string Area, string Problem);

/// <summary>Remembers which rooms have already been reported as impossible to set up.</summary>
// The whole point is to survive a restart, since a restart is when the repeat happens. A null memory reads as
// "nothing remembered", which notifies every start: the behaviour without one.
public interface IAreaSetupMemory
{
	/// <summary>Records the problems standing now, and answers which of them have not been reported before.</summary>
	/// <remarks>
	///     Always call it, an empty list included: a room that resolves is what clears its memory, and without that
	///     a problem could never be reported twice.
	/// </remarks>
	IReadOnlyList<AreaSetupFault> Record(IReadOnlyList<AreaSetupFault> standing);
}

/// <summary>The file's contents: one line per room that could not be set up, and enough context for whoever opens it.</summary>
// Shaped like LastPeriodDocument, so the notes beside the configuration document all read alike.
public sealed class AreaSetupMemoryDocument
{
	public const string Explanation =
		"Machine-written note: which rooms Adaptive Heating could not set up, and what was wrong with each. It "
		+ "exists so a standing problem is reported once instead of at every start. Not configuration - nothing "
		+ "here is edited by hand, and deleting this file is safe: the only cost is that every standing problem "
		+ "is reported once more.";

	// Bumped only when an older file could be misread.
	public const int CurrentVersion = 1;

	[JsonPropertyName(JsonNoteFile.CommentProperty)]
	[JsonPropertyOrder(JsonNoteFile.CommentOrder)]
	public string Comment { get; set; } = Explanation;

	[JsonPropertyName(JsonNoteFile.VersionProperty)]
	[JsonPropertyOrder(JsonNoteFile.VersionOrder)]
	public int Version { get; set; } = CurrentVersion;

	// Context for a human reading the file; nothing reasons from it.
	[JsonPropertyName(JsonNoteFile.SavedAtProperty)]
	public DateTimeOffset SavedAt { get; set; }

	/// <summary>Room key to the problem reported for it.</summary>
	// The problem's own words are the value, so a room whose problem changes reads as a different entry and is
	// reported again.
	[JsonPropertyName("rooms")]
	public Dictionary<string, string> Rooms { get; set; } = new(StringComparer.OrdinalIgnoreCase);

	public static readonly JsonSerializerOptions SerializerOptions = JsonNoteFile.CreateSerializerOptions();
}
