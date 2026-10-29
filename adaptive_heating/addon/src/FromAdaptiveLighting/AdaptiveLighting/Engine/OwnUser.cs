using System.Reactive.Disposables;
using System.Text.Json;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Ha;

using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Engine;

/// <summary>Learns the Home Assistant user the engine runs as, from its own snapshot event coming back.</summary>
// The event goes out on the same token as every service call, so Home Assistant stamps both with one user.
// Only an echo of a timestamp sent here counts: anything else firing the event type could name another user.
internal sealed class OwnUser : IStatePublisher, IDisposable
{
	internal const int RememberedSnapshots = 64;

	private readonly IStatePublisher _inner;
	private readonly ILogger _logger;
	private readonly Lock _gate = new();
	private readonly Queue<DateTimeOffset> _sent = new();
	private readonly IDisposable _subscription;
	private string? _userId;

	public OwnUser(IHaContext ha, IStatePublisher inner, ILogger logger)
	{
		ArgumentNullException.ThrowIfNull(ha);
		_inner = inner ?? throw new ArgumentNullException(nameof(inner));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));

		try
		{
			_subscription = ha.Events
				.Where(@event => string.Equals(@event.EventType, HaStatePublisher.EventType, StringComparison.Ordinal))
				.SubscribeSafe(Learn, logger);
		}
		catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
		{
			logger.LogWarning(exception, "Could not watch the engine's own events, so its own user is not learned.");
			_subscription = Disposable.Empty;
		}
	}

	/// <summary>The engine's own user id, or <c>null</c> until its first snapshot has come back.</summary>
	public string? UserId
	{
		get
		{
			lock (_gate)
				return _userId;
		}
	}

	public void Publish(AreaSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		lock (_gate)
		{
			if (_userId is null)
			{
				_sent.Enqueue(snapshot.Timestamp);
				while (_sent.Count > RememberedSnapshots)
					_sent.Dequeue();
			}
		}

		_inner.Publish(snapshot);
	}

	private void Learn(Event @event)
	{
		if (@event.Context?.UserId is not { Length: > 0 } userId
			|| @event.DataElement is not { ValueKind: JsonValueKind.Object } data
			|| !data.TryGetProperty("timestamp", out JsonElement stamp)
			|| stamp.ValueKind != JsonValueKind.String
			|| !stamp.TryGetDateTimeOffset(out DateTimeOffset sentAt))
			return;

		lock (_gate)
		{
			if (_userId is not null || !_sent.Contains(sentAt))
				return;

			_userId = userId;
			_sent.Clear();
		}

		_logger.LogInformation("Light changes made as Home Assistant user {UserId} are the engine's own.", userId);
	}

	public void Dispose() => _subscription.Dispose();
}
