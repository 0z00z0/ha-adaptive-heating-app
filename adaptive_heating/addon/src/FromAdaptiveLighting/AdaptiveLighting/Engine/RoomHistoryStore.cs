using System.Text.Json;
using System.Text.Json.Serialization;

namespace Adaptive.Engine;

/// <summary>Remembers each room's history, so a restart does not forget it.</summary>
// Keyed the same way carry-over on save is: by area id where there is one, else by name.
public interface IRoomHistoryStore
{
	/// <summary>What was on disk at start, or empty when there is none.</summary>
	IReadOnlyDictionary<string, AreaHistory> Load();

	/// <summary>Records the history standing now for every room, reporting failure and never throwing.</summary>
	bool TrySave(IReadOnlyDictionary<string, AreaHistory> rooms);

	/// <summary>Writes whatever is waiting, without waiting for the next coalesced interval.</summary>
	bool Flush();
}

/// <summary>The file's contents: one room's history per key, and enough context for whoever opens it.</summary>
// Shaped like LastPeriodDocument, so the notes beside the configuration document all read alike.
public sealed class RoomHistoryDocument
{
	public const string Explanation =
		"Machine-written note: each room's last movement, last change and who changed it, kept so a restart does "
		+ "not forget them. Not configuration - nothing here is edited by hand, and deleting this file is safe: "
		+ "each room starts again with its history unknown until its next event.";

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

	/// <summary>Room key (area id, or name where there is none) to its history.</summary>
	[JsonPropertyName("rooms")]
	public Dictionary<string, AreaHistory> Rooms { get; set; } = new(StringComparer.OrdinalIgnoreCase);

	public static readonly JsonSerializerOptions SerializerOptions = JsonNoteFile.CreateSerializerOptions();
}
