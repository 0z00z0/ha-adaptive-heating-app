namespace Adaptive.Engine;

/// <summary><see cref="ILastPeriodStore"/> over one declared state file in the state folder.</summary>
// Written on every change and never batched: the write a flush timer would delay is the one a restart is about
// to need. A failed write stays waiting, so the flusher writes it again. Nothing here throws; every failure is a
// warning and an answer of "unknown".
internal sealed class LastPeriodStore : ILastPeriodStore
{
	public const string NameSuffix = ".last-period.json";

	/// <summary>The declaration: the file, the version this build writes, and when it is written.</summary>
	public static readonly StateStoreDeclaration<LastPeriodDocument> Declaration = new(
		Name: "the period last run in",
		NameSuffix: NameSuffix,
		Version: LastPeriodDocument.CurrentVersion,
		WritePolicy: StateWritePolicy.Immediate,
		VersionOf: document => document.Version,
		SavedAtOf: document => document.SavedAt);

	private readonly ILogger<LastPeriodStore> _logger;
	private readonly StateStore<LastPeriodDocument> _store;
	private readonly Func<DateTimeOffset> _now;

	/// <summary>Creates a store whose file sits in the state folder beside <paramref name="configFilePath"/>.</summary>
	/// <remarks>Without a registry the note is still written and read the same way; only the shared flusher and the start report are missing.</remarks>
	/// <exception cref="ArgumentException"><paramref name="configFilePath"/> is blank or has no directory.</exception>
	public LastPeriodStore(
		string configFilePath,
		ILogger<LastPeriodStore> logger,
		StateStoreRegistry? registry = null,
		Func<DateTimeOffset>? now = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(configFilePath);

		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		_now = now ?? (() => DateTimeOffset.UtcNow);

		_store = registry is not null
			? registry.Open(Declaration, LastPeriodDocument.SerializerOptions, _logger)
			: new StateStore<LastPeriodDocument>(configFilePath, Declaration, LastPeriodDocument.SerializerOptions, _logger);
	}

	/// <summary>The directory the file lives in, which is the state folder beside the configuration document.</summary>
	public string DirectoryPath => _store.DirectoryPath;

	public string FilePath => _store.FilePath;

	/// <inheritdoc/>
	public string? Load()
	{
		LastPeriodDocument? document = _store.Restore(_now());

		if (document is null && _store.LastRestore is { Outcome: StateRestore.Discarded })
			_logger.LogWarning(
				"The engine does not know which period it was last running in. Nothing is assumed from that: a period's "
				+ "house mode is left alone until the next boundary comes round with the engine running, and {Path} is "
				+ "rewritten as soon as the period changes.",
				FilePath);

		// No file is a first run, and not a boundary crossing.
		return document?.Period is { Length: > 0 } period ? period.Trim() : null;
	}

	/// <inheritdoc/>
	public bool TrySave(string periodKey)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(periodKey);

		LastPeriodDocument document = new() { SavedAt = _now(), Period = periodKey.Trim() };

		return _store.Write(document);
	}
}
