using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Subjects;

using Adaptive.Settings;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using NetDaemon.HassModel.Entities;

namespace Adaptive.Engine;

/// <summary>The mode brain: reads the house-mode select, derives the active kind, and owns the set, retain and reset lifecycle.</summary>
// HouseState is never mutated here: every mode change is a select_option that comes back through Home Assistant.
public sealed class ModeMonitor : IDisposable
{
	private readonly IHaContext _ha;
	private readonly GlobalConfig _global;
	private readonly string? _defaultKillSwitchEntity;
	private readonly ILogger _logger;
	private readonly IScheduler _scheduler;
	private readonly IReadOnlyList<TimePeriodConfig> _periods;
	private readonly CircadianCalculator _circadian;
	private readonly IReadOnlyCollection<string> _areaMotionSensors;

	// Null when no period select is configured. Which direction it grants is its own to say.
	private readonly PeriodSelectReader? _periodSelect;

	// Wakes this monitor at the boundary itself, so a period's SetsModeId and the period mirror do not wait out a
	// whole PassCadenceSeconds. The tick below is the safety net and still runs.
	private readonly BoundaryTimer _boundary;

	// The house's sun entity announcing that it has moved. Null for a house whose boundaries the tick alone re-arms.
	private readonly IObservable<Unit>? _sunMoved;

	private readonly Subject<Unit> _changed = new();
	private readonly CompositeDisposable _subscriptions = [];

	// The one lock both halves take. Nothing that calls Home Assistant runs under it.
	private readonly object _gate = new();

	private readonly HouseModeRules _modes;
	private readonly PeriodTracker _periodTracker;

	private bool _started;
	private bool _disposed;

	// areaMotionSensors is the union across every area, and an option with an empty ResetPresenceSensors resets on
	// any of them. Without a lastPeriod the monitor never learns that a boundary was crossed during an outage.
	public ModeMonitor(
		IHaContext ha,
		GlobalConfig global,
		ILogger logger,
		IScheduler scheduler,
		IReadOnlyList<TimePeriodConfig> periods,
		Func<SunTimes> sunTimes,
		IReadOnlyCollection<string> areaMotionSensors,
		ILastPeriodStore? lastPeriod = null,
		PeriodSelectReader? periodSelect = null,
		TimeZoneInfo? zone = null,
		IObservable<Unit>? sunMoved = null,
		bool afterSave = false,
		string? defaultKillSwitchEntity = null)
	{
		_sunMoved = sunMoved;
		_defaultKillSwitchEntity = defaultKillSwitchEntity;
		_ha = ha ?? throw new ArgumentNullException(nameof(ha));
		_global = global ?? throw new ArgumentNullException(nameof(global));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		_scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
		_periods = periods ?? throw new ArgumentNullException(nameof(periods));
		ArgumentNullException.ThrowIfNull(sunTimes);
		_areaMotionSensors = areaMotionSensors ?? throw new ArgumentNullException(nameof(areaMotionSensors));

		_periodSelect = periodSelect;

		TimeZoneInfo resolvedZone = zone ?? TimeZoneInfo.Local;

		_circadian = new CircadianCalculator(periods, global, sunTimes, periodSelect?.ReadPeriod, resolvedZone);

		// Lighting's orchestrator reports these per room; heating's one table is the house's.
		foreach (DroppedPeriod drop in _circadian.DroppedPeriods)
			LogDroppedPeriod(drop);

		_circadian.PeriodDropped += LogDroppedPeriod;

		if (periodSelect is { } select)
			_logger.LogInformation(
				"Period select {Entity}: {Authority} decides the time of day.",
				select.Entity,
				select.ReadPeriod is not null ? "Home Assistant" : "adaptive heating (the select mirrors the schedule)");

		_modes = new HouseModeRules(
			_ha, _global, defaultKillSwitchEntity, _logger, _scheduler, _areaMotionSensors, _gate, _subscriptions,
			() => IsDisposed, RaiseChanged);

		_periodTracker = new PeriodTracker(
			_ha, _logger, _periods, _circadian, lastPeriod, periodSelect, _modes, _gate, () => IsDisposed, RaiseChanged,
			afterSave);

		_boundary = new BoundaryTimer(_scheduler, () => _circadian.NextBoundary(_scheduler.Now), OnTick, _logger);
	}

	/// <summary>Fires whenever the kill switch or the house-mode select changes state.</summary>
	public IObservable<Unit> Changed => _changed;

	/// <summary>Whether the engine is currently forbidden from commanding anything.</summary>
	public bool KillSwitchActive => _modes.KillSwitchActive;

	/// <summary>Whether the master switch reading <paramref name="state"/> pauses the engine.</summary>
	// The one copy of the rule; pages call this too.
	public static bool KillSwitchPauses(GlobalConfig global, string? defaultKillSwitchEntity, EntityState? state) =>
		HouseModeRules.KillSwitchPauses(global, defaultKillSwitchEntity, state);

	/// <summary>The house-mode option string, or <c>null</c> when the select is unconfigured or has never answered.</summary>
	public string? CurrentModeValue => _modes.CurrentModeValue;

	/// <summary>What is forcing the effective mode, or <c>null</c> when the select's own value is the whole story.</summary>
	public ForcedMode? Forced => _modes.Forced;

	/// <summary>The kind of the effective option; <see cref="ModeKind.Normal"/> when nothing classifies.</summary>
	public ModeKind ActiveKind => _modes.ActiveKind;

	/// <summary>The effective option's own name as written in the document, or <c>null</c> when nothing classifies.</summary>
	public string? ActiveModeName => _modes.ActiveModeName;

	/// <summary>When the mode standing now was chosen: the select's own last change, never the start of this run.</summary>
	public DateTimeOffset ActivatedAt => _modes.ActivatedAt;

	/// <summary>Subscribes and starts the evaluation tick.</summary>
	public void Start()
	{
		string? startingPeriod;

		lock (_gate)
		{
			if (_started)
				return;

			_started = true;

			// Both halves seed under the one gate. A file read is safe here only because nothing else is running yet.
			_modes.StampStart(_scheduler.Now);
			startingPeriod = _periodTracker.BeginRun(_scheduler.Now);
		}

		if (_global.EffectiveGateEntity(_defaultKillSwitchEntity) is { Length: > 0 } killSwitch)
		{
			_logger.LogInformation("Watching kill switch {EntityId}.", killSwitch);
			_subscriptions.Add(_ha.Entity(killSwitch)
				.StateChanges()
				.SubscribeSafe(
					_ =>
					{
						// Coming back from the master switch crosses no boundary, so nothing else re-asserts the
						// helper and it would read the period paused in until the next tick.
						if (!KillSwitchActive)
						{
							_periodTracker.MirrorPeriodSelect(_circadian.ActivePeriodId(_scheduler.Now));

							// A grace that ran out while the engine was muzzled wrote nothing and left nothing armed.
							_modes.ArmGraceExpiryCheck();
						}

						RaiseChanged();
					},
					_logger));
		}

		_modes.Subscribe();

		// Watched in either direction. Under Home Assistant authority a flip is a boundary the clock did not cross,
		// and waiting for the tick would delay a SetsMode by a whole PassCadenceSeconds. Under the engine's own
		// authority it fires on the engine's echo and costs one idempotent re-evaluation.
		if (_periodSelect is { } periodSelect)
		{
			_logger.LogInformation("Watching period select {EntityId}.", periodSelect.Entity);
			_subscriptions.Add(_ha.Entity(periodSelect.Entity)
				.StateChanges()
				.SubscribeSafe(_ => OnPeriodSelectChanged(), _logger));
		}

		ModeStartupReporter.AnnounceUnreachableAway(_logger, _global);
		ModeStartupReporter.AnnounceDormantModeRules(_logger, _global, _periods, _modes.HouseModeIsHomeAssistants);

		_modes.SubscribePresenceResets();
		_modes.ArmGraceExpiryCheck();
		_modes.SubscribeActivationSensors();
		SubscribeMotion();

		// An entity already on before start forces the mode from the first instant and raises no edge, so a house
		// booting into a forced mode would otherwise say nothing until somebody toggled the entity.
		_modes.AnnounceForcedMode();

		// SchedulePeriodic's first callback is a whole PassCadenceSeconds away, so without this a restart inside a
		// period leaves the select naming the period before it. A no-op under Home Assistant's authority.
		_periodTracker.MirrorPeriodSelect(startingPeriod);

		// A moved sun time can put a boundary in the past as easily as the future, so this evaluates before it re-arms.
		if (_sunMoved is { } sunMoved)
			_subscriptions.Add(sunMoved.SubscribeSafe((Unit _) => OnTick(), _logger));

		_subscriptions.Add(_scheduler.SchedulePeriodic(
			TimeSpan.FromSeconds(_global.PassCadenceSeconds),
			OnTick));

		_boundary.Arm();
	}

	/// <summary>The period select moved, so the period may have changed without the clock crossing anything.</summary>
	// Runs the whole tick body, which is idempotent: period entry is edge-triggered on a name change, the
	// inactivity rule is latched, and the mirror write compares against what the select reads.
	private void OnPeriodSelectChanged()
	{
		OnTick();
		RaiseChanged();
	}

	// Subscribes the motion union once, for auto-away by inactivity.
	private void SubscribeMotion()
	{
		bool autoAway = _modes.HasAutoAwayRule;

		if (!autoAway)
			return;

		if (_areaMotionSensors.Count == 0)
		{
			ModeStartupReporter.AnnounceMotionCannotFire(_logger, autoAway, startsPeriods: false);
			return;
		}

		foreach (string sensor in _areaMotionSensors)
		{
			_logger.LogInformation("Watching motion sensor {EntityId}.", sensor);
			_subscriptions.Add(_ha.Entity(sensor).WhenTurnsOn(_ => OnMotion(), _logger));
		}
	}

	private void OnMotion()
	{
		if (IsDisposed)
			return;

		_modes.MarkMotion();
	}

	/// <summary>One evaluation of the clock's news, handed to both halves in the order the engine decides in.</summary>
	private void OnTick()
	{
		if (IsDisposed)
			return;

		DateTimeOffset now = _scheduler.Now;

		// First, so everything below reads a mode the select has either taken or been shown not to have taken.
		_modes.ExpireAssumedMode();

		// Read outside the gate: these consult Home Assistant.
		HouseModeOptionConfig? activeOption = _modes.CurrentOption;
		bool modeIsReadable = _modes.CurrentModeValue is { Length: > 0 };

		_periodTracker.Tick(now, activeOption, modeIsReadable);

		_modes.EvaluateInactivityActivation(now);

		_boundary.Arm();
	}

	// A save disposes this monitor while a handler or timer can still be running on another thread.
	private bool IsDisposed
	{
		get { lock (_gate) return _disposed; }
	}

	private void LogDroppedPeriod(DroppedPeriod drop)
	{
		string why = drop.Reason switch
		{
			PeriodDropReason.Unparseable => "its Start could not be parsed as a clock time or a sun anchor",
			PeriodDropReason.Unresolvable => "its sun-anchored Start has no sun time to resolve against today",
			_ => "it could not be placed"
		};

		_logger.LogWarning(
			"Circadian period '{Period}' (Start '{Start}') is dropped from the table because {Why}. "
			+ "The remaining periods still cover the day by wrapping, so a boundary that should exist may be missing. "
			+ "Check this period's Start if an area lands in the wrong period.",
			drop.PeriodName, drop.Start, why);
	}

	private void RaiseChanged()
	{
		if (IsDisposed)
			return;

		try
		{
			_changed.OnNext(Unit.Default);
		}
		catch (ObjectDisposedException)
		{
			// Disposed between the check and the call.
		}
	}

	/// <inheritdoc/>
	public void Dispose()
	{
		lock (_gate)
			_disposed = true;

		_modes.Dispose();
		_boundary.Dispose();
		_subscriptions.Dispose();
		_changed.Dispose();
	}
}
