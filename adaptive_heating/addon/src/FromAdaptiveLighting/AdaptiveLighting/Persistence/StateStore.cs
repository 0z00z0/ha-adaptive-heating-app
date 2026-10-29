using System.Text.Json;

namespace Adaptive.Engine;

/// <summary>One declared state file: reads it once with the declaration's rules, and writes it under its own lock.</summary>
/// <remarks>
///     Two locks, not one. The data lock guards what is waiting to be written and is never held across a file
///     operation; the write lock is the store's single writer, so two flushes of one kind cannot race.
/// </remarks>
internal sealed class StateStore<TDocument> : IStateStore
	where TDocument : class
{
	/// <summary>How far ahead of the clock a saved time may be before the file is refused.</summary>
	// A chosen value: enough for the clock drift between two runs, far below the hours a wrong clock gives.
	public static readonly TimeSpan ClockSlack = TimeSpan.FromMinutes(1);

	private readonly StateStoreDeclaration<TDocument> _declaration;
	private readonly JsonSerializerOptions _options;
	private readonly ILogger _logger;
	private readonly JsonNoteFile _file;

	private readonly Lock _gate = new();
	private readonly Lock _writeGate = new();

	private TDocument? _pending;
	private bool _dirty;

	/// <exception cref="ArgumentException"><paramref name="configFilePath"/> is blank or has no directory.</exception>
	public StateStore(string configFilePath, StateStoreDeclaration<TDocument> declaration, JsonSerializerOptions options, ILogger logger)
	{
		_declaration = declaration ?? throw new ArgumentNullException(nameof(declaration));
		_options = options ?? throw new ArgumentNullException(nameof(options));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));

		_file = new JsonNoteFile(configFilePath, declaration.NameSuffix, logger, declaration.InStateFolder, declaration.KeepsBackup);
	}

	public StateStoreDeclaration Declaration => _declaration;

	public string Location => _file.FilePath;

	public string FilePath => _file.FilePath;

	public string DirectoryPath => _file.DirectoryPath;

	public string BackupPath => _file.BackupPath;

	public bool IsDirty
	{
		get
		{
			lock (_gate)
				return _dirty;
		}
	}

	public StateStoreRestoreReport? LastRestore { get; private set; }

	/// <summary>Reads the file and applies the declaration's rules; answers <c>null</c> for anything it will not take.</summary>
	/// <remarks>Every refusal is a warning and an answer of "unknown", never an exception.</remarks>
	public TDocument? Restore(DateTimeOffset now)
	{
		if (!_file.TryRead(_options, out TDocument? document, out bool fromBackup, out Exception? failure))
		{
			_logger.LogWarning(failure, "Could not read {Path}, so {Name} starts again as unknown.", FilePath, _declaration.Name);

			return Report(StateRestore.Discarded, "could not be read");
		}

		if (document is null)
			return Report(StateRestore.FirstRun, "no file yet");

		int version = _declaration.VersionOf(document);
		if (version > _declaration.Version)
		{
			_logger.LogWarning(
				"{Path} was written by version {Found} of {Name} and this build reads up to {Known}, so it is left alone "
				+ "and nothing is restored from it. Redeploying the newer build reads it again.",
				FilePath, version, _declaration.Name, _declaration.Version);

			return Report(StateRestore.Discarded, $"version {version} is newer than {_declaration.Version}");
		}

		DateTimeOffset savedAt = _declaration.SavedAtOf(document);
		if (savedAt > now + ClockSlack)
		{
			_logger.LogWarning(
				"{Path} says it was saved at {SavedAt}, which is ahead of the clock now reading {Now}, so it is discarded "
				+ "rather than shown as newer than now. It is written again as soon as {Name} changes.",
				FilePath, savedAt.ToString("u"), now.ToString("u"), _declaration.Name);

			return Report(StateRestore.Discarded, "saved ahead of the clock");
		}

		if (_declaration.Validate?.Invoke(document) is { Length: > 0 } refusal)
		{
			_logger.LogWarning("{Path} was not restored: {Refusal}", FilePath, refusal);

			return Report(StateRestore.Discarded, refusal);
		}

		Report(fromBackup ? StateRestore.RestoredFromBackup : StateRestore.Restored, $"saved {savedAt:u}");

		return document;
	}

	/// <summary>Takes a value to be written: at once under the immediate policy, at the next interval under the coalesced one.</summary>
	public bool Write(TDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);

		lock (_gate)
		{
			_pending = document;
			_dirty = true;
		}

		return _declaration.WritePolicy == StateWritePolicy.Immediate ? Flush() : true;
	}

	/// <inheritdoc/>
	public bool Flush()
	{
		TDocument pending;

		lock (_gate)
		{
			if (!_dirty || _pending is null)
				return true;

			pending = _pending;
		}

		bool written;
		Exception? failure;

		// Outside the data lock, so a write never blocks a lighting decision, and under the store's own writer lock.
		lock (_writeGate)
			written = _file.TryWrite(pending, _options, out failure);

		if (written)
		{
			lock (_gate)
				// A change that arrived while the write ran is still waiting, so only this value clears the flag.
				if (ReferenceEquals(_pending, pending))
					_dirty = false;

			return true;
		}

		_logger.LogWarning(
			failure,
			"Could not write {Path}. The value is still right in memory and stays waiting, so the next flush writes it again.",
			FilePath);

		return false;
	}

	/// <summary>Takes the file and its backup away, and forgets anything waiting to be written.</summary>
	public bool Remove()
	{
		lock (_gate)
		{
			_pending = null;
			_dirty = false;
		}

		bool removed;
		Exception? failure;

		lock (_writeGate)
			removed = _file.TryRemove(out failure);

		if (!removed)
			_logger.LogWarning(failure, "Could not remove {Path}. It is stale rather than wrong, and the next write replaces it.", FilePath);

		return removed;
	}

	private TDocument? Report(StateRestore outcome, string detail)
	{
		LastRestore = new StateStoreRestoreReport(_declaration.Name, FilePath, outcome, detail);

		return null;
	}
}
