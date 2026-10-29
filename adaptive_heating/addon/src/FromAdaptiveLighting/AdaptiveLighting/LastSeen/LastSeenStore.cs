namespace Adaptive.Engine;

/// <summary>One entity as it came off disk, with the bucket it was filed in and the file's own write time.</summary>
/// <remarks><c>SavedAt</c> is what settles a duplicate.</remarks>
public sealed record LoadedEntity(string EntityId, string Bucket, LastSeenEntry Entry, DateTimeOffset SavedAt);

/// <summary>Everything a load produced, including what it could not read.</summary>
/// <remarks>
///     <c>HomeAssistantStarted</c> is the newest restart estimate found in any file. <c>PreSplitRecords</c> counts
///     records from a pre-split catch-all file, all keyed <c>other</c> until the next census re-derives their class.
/// </remarks>
public sealed record LastSeenCacheLoad(
	IReadOnlyDictionary<string, LoadedEntity> Entities,
	DateTimeOffset? HomeAssistantStarted,
	int FilesRead,
	int FilesUnreadable,
	int DuplicatesResolved,
	int PreSplitRecords)
{
	/// <summary>What a first run, a deleted cache and a completely unreadable one all produce.</summary>
	public static LastSeenCacheLoad Empty { get; } =
		new(new Dictionary<string, LoadedEntity>(StringComparer.Ordinal), null, 0, 0, 0, 0);
}

/// <summary>The cache's files: where they live, how they are written, and how a torn set is read back.</summary>
/// <remarks>
///     Files are discovered from the directory, never composed from a list of buckets: a bucket is a device class, so
///     the names are not known in advance. They take their stem from the configuration document, whose directory is
///     the only path on a Home Assistant box that survives a redeploy.
/// </remarks>
public sealed class LastSeenStore : IStateStore
{
	private const string NameInfix = ".last-seen.";
	private const string Extension = ".json";
	private const string BackupSuffix = AtomicJsonFile.BackupSuffix;

	/// <summary>The subdirectory the cache lives in, so its files do not bury the hand-edited configuration document.</summary>
	public const string FolderName = "last-seen";

	/// <summary>The declaration: the cache's own folder, no backup, and the tracker's five-minute flush.</summary>
	// One kind, many files: the bucket is a device class, so the declaration names the folder and not one file.
	internal static readonly StateStoreDeclaration<LastSeenDocument> Declaration = new(
		Name: "when each entity was last heard from",
		NameSuffix: NameInfix,
		Version: LastSeenDocument.CurrentVersion,
		WritePolicy: StateWritePolicy.Coalesced,
		VersionOf: document => document.Version,
		SavedAtOf: document => document.SavedAt,
		KeepsBackup: false,
		InStateFolder: false);

	private readonly ILogger<LastSeenStore> _logger;

	// Everything before the bucket token, e.g. "b1.last-seen.". Two hosts sharing a directory cannot collide.
	private readonly string _prefix;

	// Serialises a flush against a load; they overlap only at start-up. One writer lock for this kind of file.
	private readonly Lock _gate = new();

	private StateStoreRestoreReport? _lastRestore;

	/// <summary>Creates a store whose files sit beside <paramref name="configFilePath"/>.</summary>
	/// <remarks>Only the directory and stem of the path are used; the configuration file itself is never touched here.</remarks>
	/// <exception cref="ArgumentException"><paramref name="configFilePath"/> is blank or has no directory.</exception>
	public LastSeenStore(string configFilePath, ILogger<LastSeenStore> logger)
		: this(configFilePath, logger, registry: null)
	{
	}

	/// <summary>Creates the store and declares it in <paramref name="registry"/>, which then reports what it restored.</summary>
	internal LastSeenStore(string configFilePath, ILogger<LastSeenStore> logger, StateStoreRegistry? registry)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(configFilePath);

		_logger = logger ?? throw new ArgumentNullException(nameof(logger));

		string full = Path.GetFullPath(configFilePath);

		string documentDirectory = Path.GetDirectoryName(full)
			?? throw new ArgumentException($"'{configFilePath}' has no directory to write beside.", nameof(configFilePath));

		DirectoryPath = Path.Combine(documentDirectory, FolderName);

		// b1.yaml gives b1.last-seen.motion.json: the stem stays in the name so two houses sharing a /config cannot collide.
		string stem = Path.GetFileNameWithoutExtension(full);
		if (stem.Length == 0)
			stem = "adaptive-heating";

		_prefix = stem + NameInfix;

		// Created eagerly so PathFor names a directory that exists, and guarded because a read-only /config must cost
		// history and nothing else; TrySave creates it again anyway.
		try
		{
			Directory.CreateDirectory(DirectoryPath);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			_logger.LogDebug(exception, "Could not create {Directory}; the cache will try again on its first write.", DirectoryPath);
		}

		AdoptFilesWrittenBesideTheDocument(documentDirectory);

		registry?.Add(this);
	}

	/// <summary>What the last <see cref="Load"/> found, for the start-up report.</summary>
	StateStoreRestoreReport? IStateStore.LastRestore => _lastRestore;

	StateStoreDeclaration IStateStore.Declaration => Declaration;

	string IStateStore.Location => DirectoryPath;

	// The dirty set and the retry live in the tracker, which flushes on its own five-minute interval and on dispose.
	bool IStateStore.IsDirty => false;

	bool IStateStore.Flush() => true;

	/// <summary>Moves cache files written by an earlier build into the subfolder, and drops the <c>.bak</c> that build kept.</summary>
	/// <remarks>Failures are ignored: giving up costs a bucket that starts again as unknown, and must never stop start-up.</remarks>
	private void AdoptFilesWrittenBesideTheDocument(string documentDirectory)
	{
		try
		{
			string[] strays = Directory.Exists(documentDirectory)
				? Directory.GetFiles(documentDirectory, _prefix + "*")
				: [];

			if (strays.Length == 0)
				return;

			Directory.CreateDirectory(DirectoryPath);

			int moved = 0;
			int dropped = 0;

			foreach (string stray in strays)
			{
				string name = Path.GetFileName(stray);

				// The old scheme's backups carry nothing the moved file does not.
				if (name.EndsWith(BackupSuffix, StringComparison.OrdinalIgnoreCase))
				{
					TryDelete(stray);
					dropped++;

					continue;
				}

				if (!IsCacheFile(name))
					continue;

				try
				{
					File.Move(stray, Path.Combine(DirectoryPath, name), overwrite: true);
					moved++;
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
				{
					// Left where it is; the next start tries again, and until then its history is not read.
				}
			}

			if (moved > 0 || dropped > 0)
				_logger.LogInformation(
					"Moved {Moved} last-seen cache files into {Directory} and dropped {Dropped} backups the current build "
					+ "does not write. The configuration document is the only thing beside it now.",
					moved, DirectoryPath, dropped);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			_logger.LogDebug(exception, "Could not tidy last-seen cache files beside the document; they are left alone.");
		}
	}

	public string DirectoryPath { get; }

	/// <summary>Every cache file currently on disk, in name order.</summary>
	public IReadOnlyList<string> FilePaths => [.. EnumerateFiles()];

	/// <summary>The absolute path a bucket is written to; distinct buckets always give distinct paths.</summary>
	public string PathFor(string? bucket) =>
		Path.Combine(DirectoryPath, _prefix + LastSeenBuckets.FileToken(bucket) + Extension);

	/// <summary>Reads every cache file and merges them; never throws, and an unreadable file leaves its entities unknown.</summary>
	/// <remarks>
	///     An entity found in two files is settled by the newer file's SavedAt, which happens when a crash lands between
	///     the write to its new bucket and the rewrite of its old one. The next flush removes the loser.
	/// </remarks>
	public LastSeenCacheLoad Load()
	{
		lock (_gate)
		{
			Dictionary<string, LoadedEntity> merged = new(StringComparer.Ordinal);
			DateTimeOffset? started = null;
			int read = 0;
			int unreadable = 0;
			int duplicates = 0;
			int preSplit = 0;

			foreach (string path in EnumerateFiles())
			{
				LastSeenDocument? document = TryRead(path);

				if (document is null)
				{
					unreadable++;
					continue;
				}

				read++;

				string bucket = BucketOf(document, path);

				if (document.Version <= LastSeenDocument.PreSplitVersion
					&& string.Equals(bucket, LastSeenBuckets.Unclassified, StringComparison.Ordinal))
					preSplit += document.Entities.Count;

				if (document.HomeAssistantStarted is { } candidate && (started is null || candidate > started))
					started = candidate;

				foreach (KeyValuePair<string, LastSeenEntry> pair in document.Entities)
				{
					if (pair.Key is not { Length: > 0 } || pair.Value is null)
						continue;

					LoadedEntity loaded = new(pair.Key, bucket, pair.Value, document.SavedAt);

					if (!merged.TryGetValue(pair.Key, out LoadedEntity? existing))
					{
						merged[pair.Key] = loaded;
						continue;
					}

					duplicates++;

					if (loaded.SavedAt > existing.SavedAt)
						merged[pair.Key] = loaded;
				}
			}

			if (unreadable > 0)
				_logger.LogWarning(
					"{Count} of the last-seen cache files under {Directory} could not be read; the entities in them start again as "
					+ "unknown. Nothing is treated as dead because of it.",
					unreadable, DirectoryPath);

			if (duplicates > 0)
				_logger.LogInformation(
					"{Count} entities appeared in more than one last-seen cache file, which happens when an entity's bucket changes "
					+ "and the process stopped between the two writes. The most recently written record wins and the next flush "
					+ "tidies it up.",
					duplicates);

			if (preSplit > 0)
				_logger.LogInformation(
					"{Count} last-seen records came from a pre-split '{Bucket}' cache file. Their history is kept exactly as it was; "
					+ "the next census re-files each one under its own device class, and the old file removes itself once it is empty.",
					preSplit, LastSeenBuckets.Unclassified);

			_lastRestore = read == 0
				? new StateStoreRestoreReport(Declaration.Name, DirectoryPath, StateRestore.FirstRun, "no cache files yet")
				: new StateStoreRestoreReport(
					Declaration.Name, DirectoryPath, StateRestore.Restored,
					$"{merged.Count} entities from {read} files, {unreadable} unreadable");

			return new LastSeenCacheLoad(merged, started, read, unreadable, duplicates, preSplit);
		}
	}

	/// <summary>Writes one bucket through a temp file, or removes its file when the document is empty.</summary>
	/// <remarks>
	///     An empty bucket has no file, which is also how the pre-split catch-all disappears once its records are
	///     re-filed. Reports failure, never throws: it runs on a timer, and a read-only /config must cost only history.
	/// </remarks>
	public bool TrySave(string bucket, LastSeenDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);

		string path = PathFor(bucket);

		lock (_gate)
		{
			if (document.Entities.Count == 0)
				return TryRemove(path);

			// No .bak: the most a backup could buy back is history that degrades to unknown, never to dead.
			if (AtomicJsonFile.TryWrite(path, document, LastSeenDocument.SerializerOptions, keepBackup: false, out Exception? failure))
				return true;

			_logger.LogWarning(
				failure,
				"Could not write the last-seen cache file {Path}. The record is still correct in memory; it will be written "
				+ "again at the next flush, and losing it costs only the history.",
				path);

			return false;
		}
	}

	/// <summary>Every file in the directory that looks like one of this store's.</summary>
	/// <remarks>
	///     The pattern excludes the two neighbours of a cache file: a .bak does not end in .json, and an in-flight temp
	///     file starts with a dot instead of the stem. Matched case-insensitively, as the file systems this runs on are.
	/// </remarks>
	private IEnumerable<string> EnumerateFiles()
	{
		string[] candidates;

		try
		{
			candidates = Directory.Exists(DirectoryPath)
				? Directory.GetFiles(DirectoryPath, _prefix + "*" + Extension)
				: [];
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// An unreadable directory reads downstream as a first run: unknown, not dead.
			_logger.LogWarning(exception, "Could not list the last-seen cache files under {Directory}; every entity starts as unknown.", DirectoryPath);
			return [];
		}

		Array.Sort(candidates, StringComparer.OrdinalIgnoreCase);

		return candidates.Where(path => IsCacheFile(Path.GetFileName(path)));
	}

	private bool IsCacheFile(string fileName) =>
		fileName.Length > _prefix.Length + Extension.Length
		&& fileName.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase)
		&& fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

	/// <summary>Which bucket a loaded file holds: what the document says, falling back to what its name suggests.</summary>
	/// <remarks>The document wins because the file name is sanitised and possibly fingerprinted, so it is lossy.</remarks>
	private string BucketOf(LastSeenDocument document, string path) =>
		LastSeenBuckets.NormaliseKey(document.Bucket) is { Length: > 0 } declared
			? declared
			: LastSeenBuckets.FromToken(TokenOf(Path.GetFileName(path)));

	private string TokenOf(string fileName) =>
		fileName[_prefix.Length..^Extension.Length];

	private LastSeenDocument? TryRead(string path)
	{
		if (!AtomicJsonFile.TryRead(path, LastSeenDocument.SerializerOptions, out LastSeenDocument? document, out Exception? failure))
		{
			_logger.LogWarning(failure, "Could not read the last-seen cache file {Path}; its entities start again as unknown.", path);
			return null;
		}

		if (document is not null && document.Version > LastSeenDocument.CurrentVersion)
		{
			_logger.LogWarning(
				"The last-seen cache file {Path} was written by version {Found} and this build reads up to {Known}, so it is "
				+ "left alone and its entities start again as unknown.",
				path, document.Version, LastSeenDocument.CurrentVersion);

			return null;
		}

		return document;
	}

	/// <summary>Takes an emptied bucket's file away, and its backup with it; reports failure, never throws.</summary>
	private bool TryRemove(string path)
	{
		try
		{
			if (File.Exists(path))
				File.Delete(path);

			// No .bak is written now, so a removal is also the chance to clear one an older build left.
			if (File.Exists(path + BackupSuffix))
				File.Delete(path + BackupSuffix);

			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			_logger.LogWarning(
				exception,
				"Could not remove the emptied last-seen cache file {Path}. Its entities are filed elsewhere now, so the file is stale "
				+ "rather than wrong, and the next flush tries again.",
				path);

			return false;
		}
	}

	private static void TryDelete(string path) => AtomicJsonFile.TryDelete(path);
}
