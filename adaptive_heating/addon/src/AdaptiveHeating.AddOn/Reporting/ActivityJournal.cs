using System.Text.Json;
using System.Text.Json.Serialization;


namespace AdaptiveHeating.AddOn.Reporting;

/// <summary>Keeps the activity record's rows across a restart, so a restart does not lose what the add-on did.</summary>
// A null store reads the same as an empty one: unknown, never "nothing happened".
internal interface IActivityJournal
{
	/// <summary>The rows the previous run left behind, oldest first, or empty where there are none to be had.</summary>
	IReadOnlyList<ActivityJournalRow> Load();

	/// <summary>Records the rows to keep, oldest first, reporting failure and never throwing.</summary>
	bool TrySave(IReadOnlyList<ActivityJournalRow> rows);
}

/// <summary>One journalled row, in the order it was recorded.</summary>
/// <remarks>Mirrors the record's own entry, which this store never references.</remarks>
internal sealed record ActivityJournalRow(long Sequence, HeatingReport Report);

/// <summary>The file's contents: the rows kept, and enough context for whoever opens it.</summary>
internal sealed class ActivityJournalDocument
{
	public const string Explanation =
		"Machine-written note: the newest activity-record rows, kept so a restart does not lose what the add-on did. "
		+ "Not configuration - nothing here is edited by hand, and deleting this file is safe: the only cost is an "
		+ "empty record until new rows arrive.";

	// Bumped only when an older file could be misread.
	public const int CurrentVersion = 1;

	[JsonPropertyName(JsonNoteFile.CommentProperty)]
	[JsonPropertyOrder(JsonNoteFile.CommentOrder)]
	public string Comment { get; set; } = Explanation;

	[JsonPropertyName(JsonNoteFile.VersionProperty)]
	[JsonPropertyOrder(JsonNoteFile.VersionOrder)]
	public int Version { get; set; } = CurrentVersion;

	// Context for a person reading the file; nothing reasons from it.
	[JsonPropertyName(JsonNoteFile.SavedAtProperty)]
	public DateTimeOffset SavedAt { get; set; }

	[JsonPropertyName("rows")]
	public IReadOnlyList<ActivityJournalRow>? Rows { get; set; }

	public static JsonSerializerOptions SerializerOptions { get; } = Build();

	private static JsonSerializerOptions Build()
	{
		JsonSerializerOptions options = JsonNoteFile.CreateSerializerOptions();

		// The kind is a value in the file, so it reads as RoomHolds rather than as 2 and a kind added in the
		// middle cannot silently renumber the rest.
		options.Converters.Add(new JsonStringEnumConverter());

		return options;
	}
}

/// <summary><see cref="IActivityJournal"/> over one declared state file in the state folder.</summary>
/// <remarks>
///     Written coalesced: a burst of rows arriving between two flushes costs one write, not one per row. Declared in
///     the registry the settings file already uses, so the flusher and the start-up report cover it too.
/// </remarks>
internal sealed class ActivityJournal : IActivityJournal
{
	/// <summary>The most rows kept on disk. Matches the record's own bound, kept separate so a change to either is deliberate.</summary>
	public const int Capacity = 500;

	/// <summary>The declaration the registry opens this store's file from.</summary>
	public static StateStoreDeclaration<ActivityJournalDocument> Declaration { get; } = new(
		Name: "the activity journal",
		NameSuffix: "-activity-journal.json",
		Version: ActivityJournalDocument.CurrentVersion,
		WritePolicy: StateWritePolicy.Coalesced,
		VersionOf: document => document.Version,
		SavedAtOf: document => document.SavedAt);

	private readonly StateStore<ActivityJournalDocument> _store;
	private readonly TimeProvider _clock;

	public ActivityJournal(StateStoreRegistry registry, TimeProvider clock, ILogger logger)
	{
		ArgumentNullException.ThrowIfNull(registry);

		_clock = clock ?? throw new ArgumentNullException(nameof(clock));
		_store = registry.Open(Declaration, ActivityJournalDocument.SerializerOptions, logger);
	}

	/// <summary>Where the file sits, which is what the start-up report names.</summary>
	public string FilePath => _store.FilePath;

	/// <inheritdoc/>
	public IReadOnlyList<ActivityJournalRow> Load() => _store.Restore(_clock.GetUtcNow())?.Rows ?? [];

	/// <inheritdoc/>
	public bool TrySave(IReadOnlyList<ActivityJournalRow> rows)
	{
		ArgumentNullException.ThrowIfNull(rows);

		// Kept by sequence, not by arrival order: a caller that already bounds its own list still costs nothing here.
		IReadOnlyList<ActivityJournalRow> kept = rows.Count > Capacity
			? [.. rows.OrderBy(row => row.Sequence).TakeLast(Capacity)]
			: rows;

		return _store.Write(new ActivityJournalDocument { SavedAt = _clock.GetUtcNow(), Rows = kept });
	}
}
