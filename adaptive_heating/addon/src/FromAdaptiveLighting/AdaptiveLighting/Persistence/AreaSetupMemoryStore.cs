using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Persistence;

/// <summary><see cref="IAreaSetupMemory"/> over one declared state file in the state folder.</summary>
// Every failure degrades to "notify": an unreadable file, a discarded one and a failed write all report the
// standing problems as unreported, so the cost is a repeated card and never a silence.
internal sealed class AreaSetupMemoryStore : IAreaSetupMemory
{
	public const string NameSuffix = ".setup-faults.json";

	/// <summary>The declaration: the file, the version this build writes, and when it is written.</summary>
	public static readonly StateStoreDeclaration<AreaSetupMemoryDocument> Declaration = new(
		Name: "rooms already reported as impossible to set up",
		NameSuffix: NameSuffix,
		Version: AreaSetupMemoryDocument.CurrentVersion,
		WritePolicy: StateWritePolicy.Immediate,
		VersionOf: document => document.Version,
		SavedAtOf: document => document.SavedAt);

	private readonly ILogger<AreaSetupMemoryStore> _logger;
	private readonly StateStore<AreaSetupMemoryDocument> _store;
	private readonly Func<DateTimeOffset> _now;
	private readonly Lock _gate = new();

	/// <summary>Creates a memory whose file sits in the state folder beside <paramref name="configFilePath"/>.</summary>
	/// <remarks>Without a registry the note is still written and read the same way; only the shared flusher and the start report are missing.</remarks>
	/// <exception cref="ArgumentException"><paramref name="configFilePath"/> is blank or has no directory.</exception>
	public AreaSetupMemoryStore(
		string configFilePath,
		ILogger<AreaSetupMemoryStore> logger,
		StateStoreRegistry? registry = null,
		Func<DateTimeOffset>? now = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(configFilePath);

		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		_now = now ?? (() => DateTimeOffset.UtcNow);

		_store = registry is not null
			? registry.Open(Declaration, AreaSetupMemoryDocument.SerializerOptions, _logger)
			: new StateStore<AreaSetupMemoryDocument>(configFilePath, Declaration, AreaSetupMemoryDocument.SerializerOptions, _logger);
	}

	/// <summary>The directory the file lives in, which is the state folder beside the configuration document.</summary>
	public string DirectoryPath => _store.DirectoryPath;

	public string FilePath => _store.FilePath;

	/// <inheritdoc/>
	public IReadOnlyList<AreaSetupFault> Record(IReadOnlyList<AreaSetupFault> standing)
	{
		ArgumentNullException.ThrowIfNull(standing);

		// The room set is read and rewritten as one, so two rebuilds cannot interleave a read with a write.
		lock (_gate)
		{
			Dictionary<string, string> remembered = Read();
			Dictionary<string, string> now = new(StringComparer.OrdinalIgnoreCase);
			List<AreaSetupFault> unreported = [];

			foreach (AreaSetupFault fault in standing)
			{
				if (fault.Key is not { Length: > 0 })
					continue;

				// Last write wins on a duplicate key, which two areas configured under one name would give.
				now[fault.Key] = fault.Problem;

				if (!remembered.TryGetValue(fault.Key, out string? seen) || !string.Equals(seen, fault.Problem, StringComparison.Ordinal))
					unreported.Add(fault);
			}

			// Only what is standing now is written, so a room that has resolved is forgotten and a later regression
			// counts as new again.
			if (!SameAs(remembered, now))
			{
				if (now.Count == 0)
					_store.Remove();
				else
					_store.Write(new AreaSetupMemoryDocument { SavedAt = _now(), Rooms = now });
			}

			return unreported;
		}
	}

	private static bool SameAs(Dictionary<string, string> remembered, Dictionary<string, string> now) =>
		remembered.Count == now.Count
		&& now.All(pair => remembered.TryGetValue(pair.Key, out string? seen) && string.Equals(seen, pair.Value, StringComparison.Ordinal));

	/// <summary>What is on disk, or nothing at all; a first run, an absent file and a refused one all read the same.</summary>
	private Dictionary<string, string> Read()
	{
		Dictionary<string, string> recalled = new(StringComparer.OrdinalIgnoreCase);

		AreaSetupMemoryDocument? document = _store.Restore(_now());

		if (document is null)
		{
			if (_store.LastRestore is { Outcome: StateRestore.Discarded })
				_logger.LogWarning(
					"The engine does not know which room problems it has already reported, so every problem standing now "
					+ "is reported once more and {Path} is rewritten with them.",
					FilePath);

			return recalled;
		}

		if (document.Rooms is not { Count: > 0 } rooms)
			return recalled;

		// Copied into a new dictionary: the comparer does not survive deserialisation.
		foreach (KeyValuePair<string, string> pair in rooms)
			if (pair.Key is { Length: > 0 } && pair.Value is not null)
				recalled[pair.Key] = pair.Value;

		return recalled;
	}
}
