using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Subjects;

using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Engine;

/// <summary>The presence transitions the house reacts to; steady state is read from <see cref="PresenceMonitor.IsAnyoneHome"/>.</summary>
public enum PresenceEvent
{
	/// <summary>The last person left, and stayed gone for the debounce.</summary>
	EveryoneLeft,

	/// <summary>Somebody came home to an empty house.</summary>
	FirstPersonArrived
}

/// <summary>Watches the configured people and reports the two transitions that matter.</summary>
// Leaving is debounced, arriving is not.
public sealed class PresenceMonitor : IDisposable
{
	private const string PersonDomain = "person";
	private const string HomeState = "home";

	private readonly IHaContext _ha;
	private readonly IScheduler _scheduler;
	private readonly GlobalConfig _global;
	private readonly ILogger _logger;
	private readonly Subject<PresenceEvent> _events = new();
	private readonly CompositeDisposable _subscriptions = [];
	private readonly SerialDisposable _awayTimer = new();
	private readonly object _gate = new();
	private readonly IReadOnlyList<string> _personEntityIds;

	private bool _isAnyoneHome = true;
	private bool _awayAnnounced;
	private bool _disposed;

	public PresenceMonitor(IHaContext ha, IScheduler scheduler, GlobalConfig global, ILogger logger)
	{
		_ha = ha ?? throw new ArgumentNullException(nameof(ha));
		_scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
		_global = global ?? throw new ArgumentNullException(nameof(global));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));

		_personEntityIds = ResolvePersons();
	}

	/// <summary>The transitions, hot: subscribe before calling <see cref="Start"/>.</summary>
	public IObservable<PresenceEvent> Events => _events;

	/// <summary>Whether anybody is home right now, flipping the instant a tracker says so and before any debounce.</summary>
	public bool IsAnyoneHome
	{
		get { lock (_gate) return _isAnyoneHome; }
	}

	/// <summary>The people being watched, after discovery; empty means presence cannot be determined.</summary>
	public IReadOnlyList<string> WatchedEntityIds => _personEntityIds;

	public void Start()
	{
		if (_personEntityIds.Count == 0)
		{
			// No one to watch means permanently occupied. Never sweeping is safer than sweeping wrongly.
			_logger.LogWarning("No person entities configured or discovered; presence is assumed permanently home.");
			return;
		}

		lock (_gate)
		{
			_isAnyoneHome = _personEntityIds.Any(IsHome);

			// The opening publication tells every area the house is away, so an empty start counts as announced.
			// Left false, the first arrival is swallowed as a return inside a debounce that never ran, and an
			// engine started in an empty house never comes home at all.
			_awayAnnounced = !_isAnyoneHome;
		}

		_logger.LogInformation("Watching presence for {Count} entities: {Entities}. Anyone home: {IsAnyoneHome}.",
			_personEntityIds.Count, string.Join(", ", _personEntityIds), _isAnyoneHome);

		foreach (string entityId in _personEntityIds)
			_subscriptions.Add(_ha.Entity(entityId)
				.StateChanges()
				.SubscribeSafe(_ => Reevaluate(), _logger));
	}

	private IReadOnlyList<string> ResolvePersons()
	{
		if (_global.Persons.Count > 0)
			return [.. _global.Persons];

		return _ha.EntityIdsInDomain(PersonDomain);
	}

	private bool IsHome(string entityId) => _ha.StateIs(entityId, HomeState);

	// Both of these decide under _gate and publish outside it. Subscribers run arbitrary code.
	private void Reevaluate()
	{
		bool anyoneHome = _personEntityIds.Any(IsHome);
		bool announceArrival = false;

		lock (_gate)
		{
			if (anyoneHome == _isAnyoneHome)
				return;

			_isAnyoneHome = anyoneHome;

			if (anyoneHome)
			{
				_awayTimer.Disposable = Disposable.Empty;

				// Returning before the debounce elapsed means nobody ever left.
				announceArrival = _awayAnnounced;
				_awayAnnounced = false;
			}
			else
			{
				_logger.LogInformation("Everyone appears to have left; confirming in {Minutes} minutes.", _global.AwayDebounceMinutes);
				_awayTimer.Disposable = _scheduler.Schedule(TimeSpan.FromMinutes(_global.AwayDebounceMinutes), ConfirmAway);
			}
		}

		if (!announceArrival)
			return;

		_logger.LogInformation("First person arrived.");
		_events.OnNext(PresenceEvent.FirstPersonArrived);
	}

	// A settings save disposes the running engine, and this can already be on its way on a scheduler thread when
	// it does. Nothing out there catches a throw, and an unobserved one ends the host.
	private void ConfirmAway()
	{
		lock (_gate)
		{
			if (_disposed || _isAnyoneHome || _awayAnnounced)
				return;

			_awayAnnounced = true;
		}

		_logger.LogInformation("Everyone left.");

		try
		{
			_events.OnNext(PresenceEvent.EveryoneLeft);
		}
		catch (ObjectDisposedException)
		{
			// Disposed between the check above and this line. Whatever was listening has gone with it.
		}
	}

	/// <inheritdoc/>
	public void Dispose()
	{
		lock (_gate)
			_disposed = true;

		_subscriptions.Dispose();
		_awayTimer.Dispose();
		_events.Dispose();
	}
}
