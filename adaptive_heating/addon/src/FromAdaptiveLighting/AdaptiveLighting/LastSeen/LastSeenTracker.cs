using System.Reactive.Concurrency;
using System.Reactive.Disposables;

using NetDaemon.HassModel.Entities;

namespace Adaptive.Engine;

/// <summary>Records when each Home Assistant entity was last genuinely heard from, across a restart of either side.</summary>
/// <remarks>
///     Home Assistant resets last_updated and last_changed on every restart, so all timestamps in the house collapse
///     to the restart instant and a week-dead sensor reads the same as a healthy one. Nothing here may simplify down
///     to remembering the newest last_updated, which agrees that everything is fresh on the occasions when it is not.
/// </remarks>
public sealed class LastSeenTracker : IEntityLastSeen, IDisposable
{
	private const string DeviceClassAttribute = "device_class";

	private readonly IHaContext _ha;
	private readonly IScheduler _scheduler;
	private readonly LastSeenStore _store;
	private readonly LastSeenOptions _options;
	private readonly ILogger<LastSeenTracker> _logger;

	// Readers are lighting decisions on Home Assistant's threads, writers the census and flush on the scheduler's.
	// Everything touching the dictionary or the restart estimate holds this.
	private readonly Lock _gate = new();

	private readonly Dictionary<string, TrackedEntity> _entities = new(StringComparer.Ordinal);

	// Ordinal: the keys are already normalised by LastSeenBuckets.
	private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);

	private readonly CompositeDisposable _subscriptions = [];

	private DateTimeOffset? _haStartedAt;
	private bool _previousCensusCollapsed;
	private bool _started;

	/// <summary>Nothing is loaded, sampled or written until <see cref="Start"/>.</summary>
	/// <remarks><paramref name="scheduler"/> is the module's only clock: every "now" and every timer comes from it.</remarks>
	public LastSeenTracker(
		IHaContext ha,
		IScheduler scheduler,
		LastSeenStore store,
		LastSeenOptions options,
		ILogger<LastSeenTracker> logger)
	{
		_ha = ha ?? throw new ArgumentNullException(nameof(ha));
		_scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
		_store = store ?? throw new ArgumentNullException(nameof(store));
		_options = options ?? throw new ArgumentNullException(nameof(options));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <inheritdoc/>
	public DateTimeOffset? HomeAssistantStartedUtc
	{
		get
		{
			lock (_gate)
				return _haStartedAt;
		}
	}

	/// <inheritdoc/>
	public int TrackedCount
	{
		get
		{
			lock (_gate)
				return _entities.Count;
		}
	}

	/// <inheritdoc/>
	public DateTimeOffset? LastSeenUtc(string entityId)
	{
		if (entityId is not { Length: > 0 })
			return null;

		lock (_gate)
			return _entities.TryGetValue(entityId, out TrackedEntity? tracked) ? tracked.LastSeen : null;
	}

	/// <inheritdoc/>
	public TimeSpan? SilenceOf(string entityId) =>
		LastSeenUtc(entityId) is { } seen ? _scheduler.Now.ToUniversalTime() - seen : null;

	/// <inheritdoc/>
	public bool HasBeenSilentFor(string entityId, TimeSpan threshold) =>
		threshold > TimeSpan.Zero && SilenceOf(entityId) is { } silence && silence > threshold;

	/// <summary>Loads the cache, takes the first census inline, and arms the census and flush timers.</summary>
	/// <exception cref="InvalidOperationException">Already started.</exception>
	public void Start()
	{
		lock (_gate)
		{
			if (_started)
				throw new InvalidOperationException("The last-seen tracker has already been started.");

			_started = true;
			LoadCore();
		}

		// A second opinion only: Home Assistant fires this while the socket is down for the restart, so it usually
		// never arrives.
		try
		{
			_subscriptions.Add(_ha.Events
				.Where(@event => @event.EventType is "homeassistant_start" or "homeassistant_started")
				.SubscribeSafe(OnHomeAssistantStarted, _logger));
		}
		catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
		{
			_logger.LogWarning(exception, "Could not watch for Home Assistant start events; restarts will be detected from the entity population alone.");
		}

		TakeCensus();

		_subscriptions.Add(_scheduler.SchedulePeriodic(_options.CensusInterval, TakeCensus));
		_subscriptions.Add(_scheduler.SchedulePeriodic(_options.FlushInterval, Flush));
	}

	/// <summary>Stops sampling and writes whatever has changed, so a graceful shutdown loses nothing.</summary>
	public void Dispose()
	{
		_subscriptions.Dispose();
		Flush();
	}

	// ---- the census -------------------------------------------------------------------------

	/// <summary>Samples every entity's timestamp, decides whether Home Assistant has restarted, and only then what to believe.</summary>
	/// <remarks>
	///     Sampled instead of subscribed so the restart verdict is reached from the same sample it is applied to, and
	///     restore timestamps are never believed and then rolled back.
	/// </remarks>
	private void TakeCensus()
	{
		try
		{
			TakeCensusCore();
		}
		catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
		{
			// Runs on a timer with no caller: an unobserved exception on a thread-pool scheduler ends the whole host.
			_logger.LogWarning(exception, "A last-seen census failed and was abandoned. The record is unchanged and the next census will try again.");
		}
	}

	private void TakeCensusCore()
	{
		DateTimeOffset now = _scheduler.Now.ToUniversalTime();

		IReadOnlyList<Entity> population;

		try
		{
			population = _ha.GetAllEntities();
		}
		catch (InvalidOperationException exception)
		{
			// NetDaemon's state cache throws until its first connection to Home Assistant completes.
			_logger.LogDebug(exception, "Home Assistant is not readable yet; skipping this last-seen census.");
			return;
		}

		List<EntitySample> samples = [];
		HashSet<string> present = new(StringComparer.Ordinal);

		foreach (Entity entity in population)
		{
			EntityState? state = _ha.GetState(entity.EntityId);

			if (state is null)
				continue;

			present.Add(entity.EntityId);

			if (StampOf(state, now) is { } stamp)
				samples.Add(new EntitySample(entity.EntityId, stamp, state));
		}

		if (samples.Count == 0)
		{
			// An empty house is a connection problem. Never conclude a restart or an eviction from it.
			_logger.LogDebug("No Home Assistant entity carried a timestamp this census; nothing to record.");
			return;
		}

		lock (_gate)
		{
			DetectRestart(samples, now);

			foreach (EntitySample sample in samples)
				Record(sample, now);

			Evict(present, now);
		}
	}

	/// <summary>Decides whether this sample is a house that has just restarted, and if so when.</summary>
	/// <remarks>
	///     Only a spread population becoming tight is a restart; one already collapsed last census is simply tight.
	///     The estimate moves forwards only.
	/// </remarks>
	private void DetectRestart(List<EntitySample> samples, DateTimeOffset now)
	{
		DateTimeOffset oldest = samples[0].Stamp;
		DateTimeOffset newest = samples[0].Stamp;

		foreach (EntitySample sample in samples)
		{
			if (sample.Stamp < oldest)
				oldest = sample.Stamp;

			if (sample.Stamp > newest)
				newest = sample.Stamp;
		}

		bool collapsed = samples.Count >= _options.MinimumPopulation && newest - oldest <= _options.CollapseWindow;
		bool wasCollapsed = _previousCensusCollapsed;
		_previousCensusCollapsed = collapsed;

		if (!collapsed || wasCollapsed)
			return;

		if (_haStartedAt is { } known && oldest <= known)
			// The same restart already on record; an engine restarted moments after Home Assistant was.
			return;

		DeclareRestart(oldest, now,
			$"all {samples.Count} entity timestamps collapsed into {(newest - oldest).TotalSeconds:0}s of each other");
	}

	private void DeclareRestart(DateTimeOffset startedAt, DateTimeOffset now, string evidence)
	{
		_haStartedAt = startedAt;

		// Every file carries the estimate, so every file is now out of date.
		DirtyEveryBucket();

		_logger.LogInformation(
			"Home Assistant appears to have restarted at {StartedAt:u} ({Evidence}). Its own last_updated timestamps have all been "
			+ "reset, so nothing is believed until {TrustedFrom:u}: every entity keeps the last-seen time it already had, which is "
			+ "the whole point of this cache.",
			startedAt, evidence, startedAt + _options.StartupGrace);

		if (now - startedAt > _options.CollapseWindow)
			_logger.LogDebug("The restart was noticed {Age:0} minutes after it happened.", (now - startedAt).TotalMinutes);
	}

	private void OnHomeAssistantStarted(Event @event)
	{
		lock (_gate)
		{
			DateTimeOffset now = _scheduler.Now.ToUniversalTime();

			if (_haStartedAt is { } known && known >= now)
				return;

			// The population is about to collapse, so record that now instead of waiting to observe it.
			_previousCensusCollapsed = true;
			DeclareRestart(now, now, $"Home Assistant announced it with a {@event.EventType} event");
		}
	}

	/// <summary>Whether a Home Assistant timestamp is evidence the entity is alive, or an artefact of a restart.</summary>
	/// <remarks>Inside the grace window nothing advances, so every entity keeps the record it had before the restart.</remarks>
	private bool IsEvidence(DateTimeOffset stamp) =>
		_haStartedAt is not { } started || stamp > started + _options.StartupGrace;

	/// <summary>Files one sampled entity, creating its record if this is the first time it has been met.</summary>
	/// <remarks>
	///     A first sighting is accepted on the same test as any later one, so a fresh install against a long-running
	///     Home Assistant starts with real history. Inside the restart window it starts as unknown, never as dead.
	/// </remarks>
	private void Record(EntitySample sample, DateTimeOffset now)
	{
		string bucket = BucketOf(sample);
		bool evidence = IsEvidence(sample.Stamp);

		if (!_entities.TryGetValue(sample.EntityId, out TrackedEntity? tracked))
		{
			_entities[sample.EntityId] = new TrackedEntity(sample.EntityId, bucket, evidence ? sample.Stamp : null, now);
			_dirty.Add(bucket);
			return;
		}

		if (!string.Equals(tracked.Bucket, bucket, StringComparison.Ordinal))
		{
			// Moved, never copied: both buckets go dirty so one flush removes it from the old file and adds it to the
			// new. A record left in two files becomes two divergent histories of one entity.
			_dirty.Add(tracked.Bucket);
			tracked.Bucket = bucket;
			_dirty.Add(bucket);
		}

		if (!evidence || (tracked.LastSeen is { } seen && sample.Stamp <= seen))
			return;

		tracked.LastSeen = sample.Stamp;
		_dirty.Add(bucket);
	}

	private string BucketOf(EntitySample sample) =>
		LastSeenBuckets.Classify(sample.EntityId, sample.State.AttrString(DeviceClassAttribute), LabelsOf(sample.EntityId), _options);

	/// <summary>Marks every bucket that currently holds something; there is no list of possible buckets.</summary>
	private void DirtyEveryBucket()
	{
		foreach (TrackedEntity tracked in _entities.Values)
			_dirty.Add(tracked.Bucket);
	}

	// Both forms, as the engine matches: the document stores the id once translated, and an untranslated name has to
	// keep filing. Reading names only was why a motion label stored as an id filed nothing.
	private IEnumerable<string>? LabelsOf(string entityId)
	{
		try
		{
			return _ha.GetEntityRegistration(entityId)?.Labels?
				.SelectMany(label => new[] { label.Id, label.Name })
				.OfType<string>();
		}
		catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
		{
			// NetDaemon's registry throws until its first connection completes; filing then falls back to the device class.
			_logger.LogDebug(exception, "Could not read the labels of {EntityId} for filing.", entityId);
			return null;
		}
	}

	/// <summary>Drops records that have outlived their entity.</summary>
	/// <remarks>Only absent entities are eligible; a quiet device is never evicted, because the silence is the measurement.</remarks>
	private void Evict(HashSet<string> present, DateTimeOffset now)
	{
		List<string> absent = [.. _entities
			.Where(pair => !present.Contains(pair.Key))
			.OrderBy(pair => pair.Value.AgeAnchor)
			.Select(pair => pair.Key)];

		if (absent.Count == 0)
			return;

		HashSet<string> dropped = new(StringComparer.Ordinal);

		foreach (string entityId in absent)
			if (now - _entities[entityId].AgeAnchor > _options.Retention)
				dropped.Add(entityId);

		// Then the MaxTracked ceiling, oldest first: the order retention would have taken them in anyway.
		foreach (string entityId in absent)
		{
			if (_entities.Count - dropped.Count <= _options.MaxTracked)
				break;

			dropped.Add(entityId);
		}

		if (dropped.Count == 0)
			return;

		foreach (string entityId in dropped)
			if (_entities.Remove(entityId, out TrackedEntity? tracked))
				_dirty.Add(tracked.Bucket);

		_logger.LogInformation(
			"Dropped {Count} last-seen records for entities Home Assistant no longer reports ({Entities}). Entities it still "
			+ "reports are kept however quiet they are, because that quiet is the measurement.",
			dropped.Count, string.Join(", ", dropped.Take(10)));
	}

	// ---- persistence ------------------------------------------------------------------------

	/// <summary>Reads the cache; called once, under the lock, from <see cref="Start"/>.</summary>
	private void LoadCore()
	{
		LastSeenCacheLoad load = _store.Load();

		foreach (KeyValuePair<string, LoadedEntity> pair in load.Entities)
			_entities[pair.Key] = new TrackedEntity(
				pair.Key, pair.Value.Bucket, pair.Value.Entry.LastSeen, pair.Value.Entry.TrackedSince);

		_haStartedAt = load.HomeAssistantStarted;

		// A misfiled record, an unreadable bucket or a pre-split cache all leave the set inconsistent with what the
		// next census decides; one full write settles it, and for the pre-split case that write is the migration.
		if (load.DuplicatesResolved > 0 || load.FilesUnreadable > 0 || load.PreSplitRecords > 0)
			DirtyEveryBucket();

		if (load.FilesRead == 0)
		{
			_logger.LogInformation(
				"No last-seen cache under {Directory} yet, so every entity starts as unknown and earns a last-seen time as it "
				+ "reports. Nothing is treated as dead in the meantime.",
				_store.DirectoryPath);

			return;
		}

		_logger.LogInformation(
			"Loaded {Count} last-seen records from {Files} cache files under {Directory}; Home Assistant last started {Started}.",
			_entities.Count, load.FilesRead, _store.DirectoryPath,
			_haStartedAt is { } started ? started.ToString("u") : "at an unknown time");
	}

	/// <summary>Writes whichever buckets have changed since the last flush.</summary>
	/// <remarks>Documents are built under the lock and written outside it, so a flush never blocks a lighting decision.</remarks>
	private void Flush()
	{
		List<KeyValuePair<string, LastSeenDocument>> pending;

		lock (_gate)
		{
			if (_dirty.Count == 0)
				return;

			pending = [.. _dirty.Select(bucket => new KeyValuePair<string, LastSeenDocument>(bucket, BuildDocument(bucket)))];
			_dirty.Clear();
		}

		foreach (KeyValuePair<string, LastSeenDocument> pair in pending)
			if (!_store.TrySave(pair.Key, pair.Value))
				lock (_gate)
					// Still unwritten, so still dirty: the next flush retries instead of losing the change.
					_dirty.Add(pair.Key);
	}

	/// <summary>One bucket's file contents; an empty document tells the store to remove the file.</summary>
	private LastSeenDocument BuildDocument(string bucket)
	{
		LastSeenDocument document = new()
		{
			Bucket = bucket,
			SavedAt = _scheduler.Now.ToUniversalTime(),
			HomeAssistantStarted = _haStartedAt
		};

		foreach (TrackedEntity tracked in _entities.Values)
			if (string.Equals(tracked.Bucket, bucket, StringComparison.Ordinal))
				document.Entities[tracked.EntityId] = new LastSeenEntry(tracked.LastSeen, tracked.TrackedSince);

		return document;
	}

	// ---- reading Home Assistant's clock ------------------------------------------------------

	/// <summary>The entity's Home Assistant timestamp, in UTC, never in the future.</summary>
	/// <remarks>
	///     last_updated is preferred: a re-report of the same value with a changed attribute moves only last_updated,
	///     and that is still a report. The clamp covers a Home Assistant host whose clock runs ahead.
	/// </remarks>
	private static DateTimeOffset? StampOf(EntityState state, DateTimeOffset now)
	{
		DateTime? raw = state.LastUpdated ?? state.LastChanged;

		if (raw is not { } value)
			return null;

		DateTimeOffset stamp = value.Kind switch
		{
			DateTimeKind.Utc => new DateTimeOffset(value, TimeSpan.Zero),
			DateTimeKind.Local => new DateTimeOffset(value).ToUniversalTime(),
			// Home Assistant publishes UTC: a kindless value lost its label in the JSON reader and is never local time.
			_ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero)
		};

		return stamp > now ? now : stamp;
	}

	private sealed record EntitySample(string EntityId, DateTimeOffset Stamp, EntityState State);

	/// <summary>One entity's in-memory record; the disk shape is <see cref="LastSeenEntry"/>.</summary>
	private sealed class TrackedEntity(string entityId, string bucket, DateTimeOffset? lastSeen, DateTimeOffset trackedSince)
	{
		public string EntityId { get; } = entityId;

		public string Bucket { get; set; } = bucket;

		public DateTimeOffset? LastSeen { get; set; } = lastSeen;

		public DateTimeOffset TrackedSince { get; } = trackedSince;

		/// <summary>What ageing and eviction measure from: real evidence when there is any, else first sighting.</summary>
		public DateTimeOffset AgeAnchor => LastSeen ?? TrackedSince;
	}
}
