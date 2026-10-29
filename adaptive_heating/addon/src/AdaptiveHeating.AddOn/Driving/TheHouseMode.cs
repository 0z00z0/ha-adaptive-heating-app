using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;

using Adaptive.Settings;

using AdaptiveHeating.Planner;

using AdaptiveLighting.Engine;

using NetDaemon.HassModel;

namespace AdaptiveHeating.AddOn.Driving;

/// <summary>The house mode as lighting's rules read it, over the connection and the document that are current.</summary>
internal sealed class TheHouseMode : IDisposable
{
	private readonly Func<IHaContext?> _house;
	private readonly Func<HeatingConfig> _document;
	private readonly IScheduler _scheduler;
	private readonly string? _defaultGate;
	private readonly ILoggerFactory _loggers;
	private readonly Func<SunTimes> _sunTimes;
	private readonly ILastPeriodStore? _lastPeriod;
	private readonly TimeZoneInfo _zone;
	private readonly object _lock = new();

	private readonly Action _changed;

	private ModeMonitor? _monitor;
	private PresenceMonitor? _presence;
	private IDisposable? _presenceEvents;
	private IHaContext? _builtOver;
	private GlobalConfig? _builtFrom;

	/// <param name="changed">Asks for a pass when somebody arrives or the house empties, so the room event says so.</param>
	public TheHouseMode(
		Func<IHaContext?> house,
		Func<HeatingConfig> document,
		IScheduler scheduler,
		string? defaultGate,
		ILoggerFactory loggers,
		Func<SunTimes> sunTimes,
		ILastPeriodStore? lastPeriod,
		TimeZoneInfo zone,
		Action? changed = null)
	{
		_changed = changed ?? (() => { });
		_house = house ?? throw new ArgumentNullException(nameof(house));
		_document = document ?? throw new ArgumentNullException(nameof(document));
		_scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
		_defaultGate = defaultGate;
		_loggers = loggers ?? throw new ArgumentNullException(nameof(loggers));
		_sunTimes = sunTimes ?? throw new ArgumentNullException(nameof(sunTimes));
		_lastPeriod = lastPeriod;
		_zone = zone ?? throw new ArgumentNullException(nameof(zone));
	}

	/// <summary>The heating mode a house-mode kind runs every room in.</summary>
	public static HeatingMode HeatingModeFor(ModeKind kind) => kind switch
	{
		ModeKind.Normal => HeatingMode.Home,
		ModeKind.Sleep => HeatingMode.Night,
		ModeKind.Boost => HeatingMode.Boost,
		ModeKind.OnTheWay => HeatingMode.Home,
		ModeKind.TemporarilyAway => HeatingMode.Home,
		// Every room is held as it stands, so the mode's own temperatures never reach one.
		ModeKind.Guest => HeatingMode.Home,
		_ => HeatingMode.Away,
	};

	/// <summary>The house state now, and when its mode was chosen, or <c>null</c> where no selector is named.</summary>
	public (HouseState State, DateTimeOffset? Since) Now()
	{
		lock (_lock)
		{
			HeatingConfig document = _document();

			if (MonitorOver(_house(), document.Global, document.Periods) is not { } monitor)
				return (HouseState.Initial, null);

			// Composed as lighting's orchestrator composes it. Presence is shown and never decides the mode.
			HouseState state = new(_presence?.IsAnyoneHome ?? true, monitor.ActiveKind, monitor.KillSwitchActive)
			{
				ModeValue = monitor.CurrentModeValue,
				ActiveModeName = monitor.ActiveModeName,
				Forced = monitor.Forced
			};

			// With no selector the rules stamp the start of the run, which would beat every earlier hand change.
			DateTimeOffset? since = document.Global.HouseMode?.EntityId is { Length: > 0 } ? monitor.ActivatedAt : null;

			return (state, since);
		}
	}

	// Rebuilt when the connection or the saved document changes, as lighting rebuilds its engine on a save.
	private ModeMonitor? MonitorOver(IHaContext? house, GlobalConfig global, IReadOnlyList<TimePeriodConfig> periods)
	{
		if (_monitor is not null && ReferenceEquals(house, _builtOver) && ReferenceEquals(global, _builtFrom))
			return _monitor;

		// A save over the same connection is no boundary the engine slept through.
		bool afterSave = _monitor is not null && ReferenceEquals(house, _builtOver);

		StopMonitors();
		_builtOver = house;
		_builtFrom = global;

		if (house is null)
			return null;

		ILogger<ModeMonitor> logger = _loggers.CreateLogger<ModeMonitor>();

		_presence = new PresenceMonitor(house, _scheduler, global, _loggers.CreateLogger<PresenceMonitor>());
		_presenceEvents = _presence.Events.SubscribeSafe((PresenceEvent _) => _changed(), logger);

		_monitor = new ModeMonitor(
			house,
			global,
			logger,
			_scheduler,
			periods,
			_sunTimes,
			areaMotionSensors: [],
			_lastPeriod,
			PeriodSelectReader.For(house, global, logger),
			_zone,
			SunMoved(house, global.SunEntity),
			afterSave,
			_defaultGate);

		_presence.Start();
		_monitor.Start();

		return _monitor;
	}

	/// <summary>The people watched for home and away, after discovery; empty means presence cannot be determined.</summary>
	public IReadOnlyList<string> WatchedPeople
	{
		get { lock (_lock) return _presence?.WatchedEntityIds ?? []; }
	}

	// Lighting's orchestrator's own: only a moved rising or setting can move a sun-anchored boundary.
	private static IObservable<Unit>? SunMoved(IHaContext house, string? sunEntityId)
	{
		if (sunEntityId is not { Length: > 0 })
			return null;

		return house.Entity(sunEntityId)
			.StateAllChanges()
			.Select(change => (change.New.AttrDateTimeOffset("next_rising"), change.New.AttrDateTimeOffset("next_setting")))
			.DistinctUntilChanged()
			.Select(_ => Unit.Default);
	}

	private void StopMonitors()
	{
		_presenceEvents?.Dispose();
		_presenceEvents = null;
		_presence?.Dispose();
		_presence = null;
		_monitor?.Dispose();
		_monitor = null;
	}

	/// <inheritdoc/>
	public void Dispose()
	{
		lock (_lock)
			StopMonitors();
	}
}
