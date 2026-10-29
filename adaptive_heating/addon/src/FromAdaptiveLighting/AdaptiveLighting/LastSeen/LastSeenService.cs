using System.Reactive.Concurrency;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Adaptive.Engine;

/// <summary>Gives <see cref="LastSeenTracker"/> a lifetime and a Home Assistant connection, and answers before it has one.</summary>
/// <remarks>
///     The add-on's entity model is brought up once per connection, so a tracker runs per connection on that model and
///     hands its record over through the cache files. Every answer is unknown between two connections.
/// </remarks>
public sealed class LastSeenService : IEntityLastSeen, IHostedService, IAsyncDisposable
{
	private readonly LastSeenStore _store;
	private readonly LastSeenOptions _options;
	private readonly IScheduler _scheduler;
	private readonly ILoggerFactory _loggerFactory;
	private readonly ILogger<LastSeenService> _logger;
	private readonly Lock _gate = new();

	private LastSeenTracker? _tracker;

	/// <summary>Nothing is loaded or sampled until <see cref="CameUp"/>.</summary>
	public LastSeenService(
		LastSeenStore store,
		LastSeenOptions options,
		IScheduler scheduler,
		ILoggerFactory loggerFactory)
	{
		_store = store ?? throw new ArgumentNullException(nameof(store));
		_options = options ?? throw new ArgumentNullException(nameof(options));
		_scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
		_loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
		_logger = loggerFactory.CreateLogger<LastSeenService>();
	}

	/// <summary>The running tracker, or <c>null</c> before start-up; exposed for diagnostics only.</summary>
	public LastSeenTracker? Tracker => _tracker;

	/// <inheritdoc/>
	public DateTimeOffset? LastSeenUtc(string entityId) => _tracker?.LastSeenUtc(entityId);

	/// <inheritdoc/>
	public TimeSpan? SilenceOf(string entityId) => _tracker?.SilenceOf(entityId);

	/// <inheritdoc/>
	public bool HasBeenSilentFor(string entityId, TimeSpan threshold) => _tracker?.HasBeenSilentFor(entityId, threshold) ?? false;

	/// <inheritdoc/>
	public DateTimeOffset? HomeAssistantStartedUtc => _tracker?.HomeAssistantStartedUtc;

	/// <inheritdoc/>
	public int TrackedCount => _tracker?.TrackedCount ?? 0;

	/// <summary>Nothing to start: the tracker waits for the entity model.</summary>
	public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	/// <summary>Starts a tracker on the model brought up for this connection; never throws.</summary>
	public void CameUp(IHaContext ha)
	{
		ArgumentNullException.ThrowIfNull(ha);

		lock (_gate)
		{
			StopTracker();

			try
			{
				LastSeenTracker tracker = new(
					ha,
					_scheduler,
					_store,
					_options,
					_loggerFactory.CreateLogger<LastSeenTracker>());

				tracker.Start();
				_tracker = tracker;

				_logger.LogInformation(
					"Tracking when Home Assistant entities were last heard from; a census every {Census}s, written to {Directory} every {Flush} minutes.",
					_options.CensusInterval.TotalSeconds, _store.DirectoryPath, _options.FlushInterval.TotalMinutes);
			}
			catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
			{
				_logger.LogError(exception, "Could not start last-seen tracking. Every entity will read as unknown, which is the safe answer.");
			}
		}
	}

	/// <summary>The connection has gone: the tracker writes what changed and stops.</summary>
	public void HasGone()
	{
		lock (_gate)
			StopTracker();
	}

	/// <summary>Stops the tracker, writing whatever has changed on the way out.</summary>
	public async Task StopAsync(CancellationToken cancellationToken) => await DisposeAsync().ConfigureAwait(false);

	/// <summary>Disposes the tracker, which flushes; safe to call more than once.</summary>
	public ValueTask DisposeAsync()
	{
		HasGone();

		return ValueTask.CompletedTask;
	}

	private void StopTracker()
	{
		LastSeenTracker? tracker = _tracker;
		_tracker = null;
		tracker?.Dispose();
	}
}
