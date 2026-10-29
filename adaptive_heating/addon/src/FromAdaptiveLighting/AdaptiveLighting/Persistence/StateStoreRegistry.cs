using System.Reactive.Concurrency;
using System.Text.Json;

namespace Adaptive.Engine;

/// <summary>Where every engine-owned state file is declared, flushed and reported.</summary>
/// <remarks>
///     One registry per configuration document. It holds the declarations, hands each store its file, drives the one
///     flusher on the engine's scheduler, and answers what a start restored. The configuration document is not in
///     here: it keeps its own single write path.
/// </remarks>
internal sealed class StateStoreRegistry : IDisposable
{
	/// <summary>How often the flusher runs when a host does not choose.</summary>
	// Matches the room history note's coalescing interval, so a burst of changes costs one write a minute.
	public static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromMinutes(1);

	private readonly string _configFilePath;
	private readonly ILogger<StateStoreRegistry> _logger;
	private readonly List<IStateStore> _stores = [];
	private readonly Lock _gate = new();

	private IDisposable? _flusher;
	private bool _disposed;

	/// <summary>Opens a registry for the document at <paramref name="configFilePath"/>; without a scheduler nothing is flushed on a timer.</summary>
	/// <exception cref="ArgumentException"><paramref name="configFilePath"/> is blank.</exception>
	public StateStoreRegistry(string configFilePath, ILogger<StateStoreRegistry> logger, IScheduler? scheduler = null, TimeSpan? flushInterval = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(configFilePath);

		_configFilePath = configFilePath;
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));

		FlushInterval = flushInterval ?? DefaultFlushInterval;

		if (scheduler is not null)
			_flusher = scheduler.SchedulePeriodic(FlushInterval, FlushAll);
	}

	public TimeSpan FlushInterval { get; }

	// The heating settings note opens its own file beside this document, outside the registry.
	public string DocumentPath => _configFilePath;

	/// <summary>The state subfolder beside the configuration document, where the notes live.</summary>
	public string StateDirectoryPath =>
		Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_configFilePath)) ?? ".", JsonNoteFile.StateFolderName);

	public IReadOnlyList<IStateStore> Stores
	{
		get
		{
			lock (_gate)
				return [.. _stores];
		}
	}

	/// <summary>Declares a store and gives it its file; the flusher and the start report cover it from here on.</summary>
	public StateStore<TDocument> Open<TDocument>(StateStoreDeclaration<TDocument> declaration, JsonSerializerOptions options, ILogger logger)
		where TDocument : class
	{
		ArgumentNullException.ThrowIfNull(declaration);

		StateStore<TDocument> store = new(_configFilePath, declaration, options, logger);
		Add(store);

		return store;
	}

	/// <summary>Declares a store that builds its own files, such as the bucketed last-seen cache.</summary>
	public void Add(IStateStore store)
	{
		ArgumentNullException.ThrowIfNull(store);

		lock (_gate)
			_stores.Add(store);
	}

	/// <summary>Writes one line per store saying what the start restored or discarded.</summary>
	/// <remarks>Without it a person cannot tell a restored fact from a fresh one after a deploy.</remarks>
	public void ReportRestored()
	{
		foreach (IStateStore store in Stores)
		{
			StateStoreRestoreReport report = store.LastRestore
				?? new StateStoreRestoreReport(store.Declaration.Name, store.Location, StateRestore.FirstRun, "not read");

			_logger.LogInformation(
				"State at start: {Name} {Outcome} from {Location} ({Detail}).",
				report.Name, Wording(report.Outcome), report.Location, report.Detail);
		}
	}

	/// <summary>Writes every store that has something waiting; a store that fails stays dirty for the next interval.</summary>
	public void FlushAll()
	{
		foreach (IStateStore store in Stores)
			store.Flush();
	}

	/// <summary>Stops the flusher and writes everything still waiting.</summary>
	public void Dispose()
	{
		if (_disposed)
			return;

		_disposed = true;

		_flusher?.Dispose();
		_flusher = null;

		FlushAll();
	}

	private static string Wording(StateRestore outcome) => outcome switch
	{
		StateRestore.FirstRun => "found nothing",
		StateRestore.Restored => "restored",
		StateRestore.RestoredFromBackup => "restored from its backup",
		_ => "discarded what it found"
	};
}
