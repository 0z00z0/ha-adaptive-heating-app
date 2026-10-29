namespace Adaptive.Engine;

/// <summary><see cref="IRoomHistoryStore"/> over one declared state file in the state folder.</summary>
/// <remarks>
///     Written coalesced: motion and light changes across every room mark the file dirty, and the registry's
///     flusher writes it at most once a minute. <see cref="Flush"/> is the extra write on a settings save
///     and on shutdown, the two moments a minute's wait is not acceptable.
/// </remarks>
internal sealed class RoomHistoryStore : IRoomHistoryStore
{
	public const string NameSuffix = ".room-history.json";

	/// <summary>The declaration: the file, the version this build writes, and when it is written.</summary>
	public static readonly StateStoreDeclaration<RoomHistoryDocument> Declaration = new(
		Name: "each room's history",
		NameSuffix: NameSuffix,
		Version: RoomHistoryDocument.CurrentVersion,
		WritePolicy: StateWritePolicy.Coalesced,
		VersionOf: document => document.Version,
		SavedAtOf: document => document.SavedAt);

	private readonly StateStore<RoomHistoryDocument> _store;
	private readonly Func<DateTimeOffset> _now;

	/// <summary>Declares the store in <paramref name="registry"/>, whose flusher then writes it.</summary>
	/// <exception cref="ArgumentException">The registry's configuration path has no directory.</exception>
	public RoomHistoryStore(
		StateStoreRegistry registry,
		ILoggerFactory loggerFactory,
		Func<DateTimeOffset>? now = null)
	{
		ArgumentNullException.ThrowIfNull(registry);
		ArgumentNullException.ThrowIfNull(loggerFactory);

		_now = now ?? (() => DateTimeOffset.UtcNow);

		_store = registry.Open(Declaration, RoomHistoryDocument.SerializerOptions, loggerFactory.CreateLogger<RoomHistoryStore>());
	}

	/// <summary>The directory the file lives in, which is the state folder beside the configuration document.</summary>
	public string DirectoryPath => _store.DirectoryPath;

	public string FilePath => _store.FilePath;

	/// <inheritdoc/>
	public IReadOnlyDictionary<string, AreaHistory> Load()
	{
		RoomHistoryDocument? document = _store.Restore(_now());

		return document?.Rooms is { Count: > 0 } rooms
			? new Dictionary<string, AreaHistory>(rooms, StringComparer.OrdinalIgnoreCase)
			: new Dictionary<string, AreaHistory>(StringComparer.OrdinalIgnoreCase);
	}

	/// <inheritdoc/>
	public bool TrySave(IReadOnlyDictionary<string, AreaHistory> rooms)
	{
		ArgumentNullException.ThrowIfNull(rooms);

		return _store.Write(new RoomHistoryDocument
		{
			SavedAt = _now(),
			Rooms = new Dictionary<string, AreaHistory>(rooms, StringComparer.OrdinalIgnoreCase)
		});
	}

	/// <inheritdoc/>
	public bool Flush() => _store.Flush();
}
