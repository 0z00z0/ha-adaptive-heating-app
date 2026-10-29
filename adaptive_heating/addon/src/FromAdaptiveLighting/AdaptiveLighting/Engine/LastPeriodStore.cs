using System.Text.Json;
using System.Text.Json.Serialization;

namespace Adaptive.Engine;

/// <summary>Remembers the circadian period last run in, so a restart can tell whether a boundary went by.</summary>
// A null store reads the same as an empty one: unknown, never "a boundary was crossed".
public interface ILastPeriodStore
{
	/// <summary>The period the previous run recorded, or <c>null</c> when there is none to be had.</summary>
	// A first run, a deleted file and a corrupt one all answer null. The value is a TimePeriodConfig.Id, or a
	// period name in a file written before ids existed; ModeMonitor translates the older shape.
	string? Load();

	/// <summary>Records the period now current, by key, reporting failure and never throwing.</summary>
	bool TrySave(string periodKey);
}

/// <summary>The file's contents: the period name, and enough context for whoever opens it.</summary>
// Shaped like LastSeenDocument, so the two files read alike.
public sealed class LastPeriodDocument
{
	public const string Explanation =
		"Machine-written note: which circadian period Adaptive Heating was last running in. Read once at start-up, "
		+ "to work out whether a period boundary went by while the engine was stopped. Not configuration - nothing "
		+ "here is edited by hand, and deleting this file is safe: the only cost is that the first start after "
		+ "deleting it will not re-apply a period's house mode.";

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

	/// <summary>The period's key, never the moment its boundary fell.</summary>
	// A sun-anchored Start resolves to a different time every day, so a stored timestamp would have to be re-read
	// against a table that has moved, and a box back from an outage may have an uncorrected clock. Comparing two
	// keys touches neither. A file from before ids holds a period name here.
	[JsonPropertyName("period")]
	public string? Period { get; set; }

	public static readonly JsonSerializerOptions SerializerOptions = JsonNoteFile.CreateSerializerOptions();
}
