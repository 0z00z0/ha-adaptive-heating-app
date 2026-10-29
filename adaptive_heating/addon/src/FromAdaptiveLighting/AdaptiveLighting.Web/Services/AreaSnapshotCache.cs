using System.Collections.Concurrent;
using System.Reactive.Subjects;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Ha;

namespace AdaptiveLighting.Web.Services;

/// <summary>The last <see cref="AreaSnapshot"/> seen per area, and the dashboard's only source of engine state.</summary>
// Fed from the adaptive_heating_area event, so a snapshot makes a round trip through Home Assistant: nothing shows
// without a live connection. The add-on's model comes and goes with the connection, hence CameUp and HasGone.
public sealed class AreaSnapshotCache : IDisposable
{
	/// <summary>How many reports the lanes' history keeps before the oldest falls off.</summary>
	public const int Capacity = 500;

	private readonly ILogger<AreaSnapshotCache> _logger;
	private readonly ConcurrentDictionary<string, AreaSnapshot> _snapshots = new(StringComparer.Ordinal);
	private readonly Subject<AreaSnapshot> _changes = new();
	private readonly Lock _gate = new();
	private readonly Queue<AreaSnapshot> _history = new(Capacity);

	private IDisposable? _subscription;

	public AreaSnapshotCache(ILogger<AreaSnapshotCache> logger) =>
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));

	/// <summary>Every area the cache has heard from, newest state per area, ordered by name.</summary>
	public IReadOnlyList<AreaSnapshot> Snapshots =>
		[.. _snapshots.Values.OrderBy(snapshot => snapshot.AreaName, StringComparer.CurrentCulture)];

	/// <summary>Pushes each snapshot as it arrives, so a component can re-render without polling.</summary>
	public IObservable<AreaSnapshot> Changes => _changes;

	public bool HasData => !_snapshots.IsEmpty;

	/// <summary>When the first snapshot arrived: the board cannot see further back.</summary>
	public DateTimeOffset? Since { get; private set; }

	/// <summary>Every report still held, oldest first, for the board's lanes.</summary>
	public IReadOnlyList<AreaSnapshot> History
	{
		get
		{
			lock (_gate)
				return [.. _history];
		}
	}

	/// <summary>A room's newest snapshot, by area id first and display name second, or <c>null</c> when it has not
	/// reported.</summary>
	public AreaSnapshot? Find(string? areaId, string? name)
	{
		if (areaId is { Length: > 0 } id
			&& _snapshots.TryGetValue(id, out AreaSnapshot? byId)
			&& string.Equals(byId.AreaId, id, StringComparison.Ordinal))
			return byId;

		return name is { Length: > 0 }
			? Snapshots.FirstOrDefault(snapshot => string.Equals(snapshot.AreaName, name, StringComparison.OrdinalIgnoreCase))
			: null;
	}

	/// <summary>What a snapshot is filed under: its area id, or its name when it has none.</summary>
	public static string KeyOf(AreaSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		return snapshot.AreaId is { Length: > 0 } areaId ? areaId : snapshot.AreaName;
	}

	/// <summary>Watches the model this connection brought up.</summary>
	public void CameUp(IHaContext ha)
	{
		ArgumentNullException.ThrowIfNull(ha);

		HasGone();

		// A page that cannot subscribe is a degraded page, not a dead add-on.
		try
		{
			_subscription = ha.Events
				.Filter<AreaSnapshotEvent>(HaStatePublisher.EventType)
				.SubscribeSafe(Record, _logger);

			_logger.LogInformation(
				"Watching Home Assistant for {EventType} events; the dashboard fills in as rooms report.",
				HaStatePublisher.EventType);
		}
		catch (Exception exception)
		{
			_logger.LogError(exception, "Could not subscribe to area events. The dashboard will stay empty.");
		}
	}

	/// <summary>Drops the subscription with the connection; what was heard stays on the page.</summary>
	public void HasGone()
	{
		_subscription?.Dispose();
		_subscription = null;
	}

	/// <summary>Files one snapshot as though it had arrived as an event.</summary>
	public void Take(AreaSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		// Keyed on the registry area id, so a room renamed while a page is open replaces itself.
		_snapshots[KeyOf(snapshot)] = snapshot;

		lock (_gate)
		{
			Since ??= snapshot.Timestamp;
			_history.Enqueue(snapshot);

			while (_history.Count > Capacity)
				_history.Dequeue();
		}

		_changes.OnNext(snapshot);
	}

	/// <inheritdoc/>
	public void Dispose() => HasGone();

	private void Record(Event<AreaSnapshotEvent> @event)
	{
		if (@event.Data?.ToSnapshot() is { } snapshot)
			Take(snapshot);
	}
}
