using System.Text.Json;
using System.Text.Json.Serialization;


namespace AdaptiveHeating.AddOn.Reporting;

/// <summary>One warm-up the add-on asked for, until it has been written into the record.</summary>
/// <param name="Key">The room and the deadline together, which is what says two rows are the same warm-up.</param>
internal sealed record WarmUpAsked(
	string Key,
	string RoomId,
	double Wanted,
	DateTimeOffset Started,
	DateTimeOffset Deadline)
{
	/// <summary>The room's temperature at the moment the deadline passed, or <c>null</c> where it had none.</summary>
	public double? Reached { get; init; }

	/// <summary>Whether the room was at the temperature it was asked for. No reading counts as not reached.</summary>
	public bool Met => Reached is { } got && got + 1e-9 >= Wanted;
}

/// <summary>Keeps the warm-ups awaiting the record across a restart.</summary>
// A null store reads the same as an empty one: nothing was asked for, never a row that was silently dropped.
internal interface IWarmUpNote
{
	/// <summary>The rows the previous run left behind, or empty where there are none to be had.</summary>
	IReadOnlyList<WarmUpAsked> Load();

	/// <summary>Records the rows still waiting, reporting failure and never throwing.</summary>
	bool TrySave(IReadOnlyList<WarmUpAsked> rows);
}

/// <summary>The file's contents: the warm-ups still waiting to be written down.</summary>
internal sealed class WarmUpNoteDocument
{
	public const string Explanation =
		"Machine-written note: the warm-ups this add-on asked for and has not yet written into the record calendar. "
		+ "Not configuration - nothing here is edited by hand. Deleting this file loses the entries not yet written, "
		+ "and can let one already written be written a second time.";

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

	[JsonPropertyName("asked")]
	public IReadOnlyList<WarmUpAsked>? Asked { get; set; }

	public static JsonSerializerOptions SerializerOptions { get; } = JsonNoteFile.CreateSerializerOptions();
}

/// <summary><see cref="IWarmUpNote"/> over one declared state file in the state folder.</summary>
internal sealed class WarmUpNote : IWarmUpNote
{
	/// <summary>The declaration the registry opens this store's file from.</summary>
	public static StateStoreDeclaration<WarmUpNoteDocument> Declaration { get; } = new(
		Name: "the warm-ups awaiting the record",
		NameSuffix: "-warm-ups.json",
		Version: WarmUpNoteDocument.CurrentVersion,
		// A restart between the asking and the deadline is exactly what this file exists to survive.
		WritePolicy: StateWritePolicy.Immediate,
		VersionOf: document => document.Version,
		SavedAtOf: document => document.SavedAt);

	private readonly StateStore<WarmUpNoteDocument> _store;
	private readonly TimeProvider _clock;

	public WarmUpNote(StateStoreRegistry registry, TimeProvider clock, ILogger logger)
	{
		ArgumentNullException.ThrowIfNull(registry);

		_clock = clock ?? throw new ArgumentNullException(nameof(clock));
		_store = registry.Open(Declaration, WarmUpNoteDocument.SerializerOptions, logger);
	}

	/// <summary>Where the file sits, which is what the start-up report names.</summary>
	public string FilePath => _store.FilePath;

	/// <inheritdoc/>
	public IReadOnlyList<WarmUpAsked> Load() => _store.Restore(_clock.GetUtcNow())?.Asked ?? [];

	/// <inheritdoc/>
	public bool TrySave(IReadOnlyList<WarmUpAsked> rows)
	{
		ArgumentNullException.ThrowIfNull(rows);

		return _store.Write(new WarmUpNoteDocument { SavedAt = _clock.GetUtcNow(), Asked = rows });
	}
}
