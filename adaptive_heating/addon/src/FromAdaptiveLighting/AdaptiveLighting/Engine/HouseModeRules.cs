using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;

using AdaptiveLighting.Configuration;

using NetDaemon.HassModel.Entities;

namespace Adaptive.Engine;

/// <summary>The house-mode half of the mode brain: the select read, what forces a mode, the resets, the presence grace and auto-away.</summary>
// HouseState is never mutated here: every mode change is an input_select.select_option that flows back through
// Home Assistant and the normal Changed path. The gate is ModeMonitor's own, shared with PeriodTracker, and
// nothing that calls Home Assistant runs under it.
internal sealed class HouseModeRules : IDisposable
{
	private const string PersonDomain = "person";
	private const string DeviceTrackerDomain = "device_tracker";
	private const string HomeState = "home";

	private readonly IHaContext _ha;
	private readonly GlobalConfig _global;
	private readonly string? _defaultKillSwitchEntity;
	private readonly ILogger _logger;
	private readonly IScheduler _scheduler;
	private readonly IReadOnlyCollection<string> _areaMotionSensors;
	private readonly CompositeDisposable _subscriptions;
	private readonly Func<bool> _isDisposed;
	private readonly Action _raiseChanged;

	// ModeMonitor's gate, the one lock this engine's mode state is under. Guards every mutable field below except
	// _warnedValues and _warnedNoNormal, which carry their own atomics.
	private readonly object _gate;

	// The select this half may drive. The ownership test and the call live in SelectMirror.
	private readonly SelectMirror _modeSelect;

	// The one-shot that looks again when a presence grace runs out. Serial, so re-arming on a mode change cancels the
	// check belonging to the mode that has gone.
	private readonly SerialDisposable _graceEnds = new();

	// CurrentModeValue is read from several Rx subscriptions, so TryAdd is the atomic bit; the value is unused.
	private readonly ConcurrentDictionary<string, byte> _warnedValues = new(StringComparer.OrdinalIgnoreCase);

	// Reset fires from several subscriptions; the "no Normal target" warning must not spam the log every tick.
	private int _warnedNoNormal;

	// The last value the select reported, and whether it has stopped answering. A blind read holds the former and
	// never asserts Normal.
	private string? _lastKnownMode;
	private bool _selectIsBlind;

	// What a reset has just written and the house is already acting on, before Home Assistant echoes it. Cleared
	// by the select moving at all, and by the first tick that finds the select has not taken it.
	private string? _assumedMode;

	// Moves whenever _assumedMode is set or cleared, so the tick clears only the assumption it checked.
	private int _assumedGeneration;

	private DateTimeOffset _activatedAt;
	private DateTimeOffset _lastMotionAt;
	private bool _inactivityLatched;

	// The option the no-motion rule last wrote, so a mode the engine set can be told from the same mode a person
	// chose. Cleared the moment the select stops reading it, and never persisted: after a restart nobody knows why
	// the select stands where it does.
	private string? _inactivityActivated;

	// The last sentence AnnounceForcedMode wrote. Empty means nothing was being forced at the previous read.
	private string _announcedForce = "";

	internal HouseModeRules(
		IHaContext ha,
		GlobalConfig global,
		string? defaultKillSwitchEntity,
		ILogger logger,
		IScheduler scheduler,
		IReadOnlyCollection<string> areaMotionSensors,
		object gate,
		CompositeDisposable subscriptions,
		Func<bool> isDisposed,
		Action raiseChanged)
	{
		_ha = ha;
		_global = global;
		_defaultKillSwitchEntity = defaultKillSwitchEntity;
		_logger = logger;
		_scheduler = scheduler;
		_areaMotionSensors = areaMotionSensors;
		_gate = gate;
		_subscriptions = subscriptions;
		_isDisposed = isDisposed;
		_raiseChanged = raiseChanged;

		_modeSelect = new SelectMirror(ha, global.HouseMode?.Entity, !HouseModeIsHomeAssistants);
	}

	/// <summary>Whether the engine is currently forbidden from commanding anything.</summary>
	internal bool KillSwitchActive =>
		_global.EffectiveGateEntity(_defaultKillSwitchEntity) is { Length: > 0 } entityId
		&& KillSwitchPauses(_global, _defaultKillSwitchEntity, _ha.GetState(entityId));

	/// <summary>Whether the master switch reading <paramref name="state"/> pauses the engine.</summary>
	// The one copy of the rule; pages call this too. Unavailable or unknown pauses nothing, whichever polarity.
	// A defaulted switch is always an enabled flag (off pauses); KillSwitchActiveWhenOff governs an explicit one.
	internal static bool KillSwitchPauses(GlobalConfig global, string? defaultKillSwitchEntity, EntityState? state)
	{
		ArgumentNullException.ThrowIfNull(global);

		if (state?.State is null)
			return false;

		bool enabledFlag = global.GateIsDefaulted(defaultKillSwitchEntity) || global.GateActiveWhenOff;
		return enabledFlag ? state.IsOff() : state.IsOn();
	}

	/// <summary>The house-mode option string, or <c>null</c> when the select is unconfigured or has never answered.</summary>
	// Warns once per distinct value no option classifies, which is the tripwire for a rename in Home Assistant.
	// An unreadable select holds the mode it last reported; see HoldLastKnownMode.
	internal string? CurrentModeValue
	{
		get
		{
			if (_global.HouseMode?.Entity is not { Length: > 0 } entityId)
				return null;

			// A reset the engine has just written counts as done. The select has not moved since, so nothing it
			// reads is newer than this.
			lock (_gate)
				if (_assumedMode is { Length: > 0 } assumed)
					return assumed;

			if (_ha.GetState(entityId).AsUsableState() is not { } value)
				return HoldLastKnownMode(entityId);

			if (_global.HouseMode.OptionFor(value) is null && _warnedValues.TryAdd(value, 0))
				_logger.LogWarning(
					"House-mode select {Entity} reports '{Value}', which no option classifies; treating it as Normal.",
					entityId, value);

			return RememberMode(entityId, value);
		}
	}

	/// <summary>Records the value the select reported, and says so when it had stopped answering.</summary>
	private string RememberMode(string entityId, string value)
	{
		bool wasBlind;

		lock (_gate)
		{
			wasBlind = _selectIsBlind;
			_selectIsBlind = false;
			_lastKnownMode = value;
		}

		if (wasBlind)
			_logger.LogInformation(
				"House-mode select {Entity} is readable again and reports '{Value}'.", entityId, value);

		return value;
	}

	/// <summary>What a blind read answers: the mode the select last reported, or <c>null</c> when it never has.</summary>
	// Unavailable and unknown are the select not answering, which is not the same as it answering Normal. Falling
	// through to Normal takes a house out of away for as long as the helper is missing, and nothing puts it back.
	// The period select has carried this guard since it was written; this is the same one.
	private string? HoldLastKnownMode(string entityId)
	{
		string? held;
		bool firstOfTheSpell;

		lock (_gate)
		{
			held = _lastKnownMode;
			firstOfTheSpell = !_selectIsBlind;
			_selectIsBlind = true;
		}

		if (firstOfTheSpell)
			_logger.LogWarning(
				"House-mode select {Entity} is not answering. The house holds the mode it last read ({Mode}) until it does.",
				entityId, held ?? "none yet");

		return held;
	}

	// What the select's current value classifies to. The set, retain and reset lifecycle acts on this option,
	// because every reset writes the select itself back to Normal.
	internal HouseModeOptionConfig? CurrentOption => _global.HouseMode?.OptionFor(CurrentModeValue);

	// Home Assistant owns the select. Every write of it is gated on this, and so is every engine rule that would
	// decide the mode: the dropdown is then the only thing that moves the house.
	internal bool HouseModeIsHomeAssistants => _global.HouseMode?.HomeAssistantDecides ?? false;

	/// <summary>The option value an id names, for a period that switches the mode.</summary>
	internal string? OptionValueFor(string modeId) => _global.HouseMode?.OptionValueFor(modeId);

	/// <summary>Whether any option activates on a quiet house, which is the rule the motion union feeds.</summary>
	internal bool HasAutoAwayRule =>
		!HouseModeIsHomeAssistants
		&& _global.HouseMode is { } houseMode
		&& houseMode.Options.Any(option => option.Kind != ModeKind.Normal && option.ActivateAfterNoMotionMinutes is > 0);

	// The first option in list order any of whose ActivateWhileOn entities is on, with that entity, or null. Such
	// an option forces the effective house mode whatever the select reads, and the select is never written from
	// here, so there is no loop. The only read of which entity holds a mode on: a second one could name a
	// different entity from the one the engine acted on.
	private (HouseModeOptionConfig Option, string EntityId)? ActivatedNow
	{
		get
		{
			// Forcing a mode is this engine deciding one, which is what standing down means.
			if (HouseModeIsHomeAssistants || _global.HouseMode is not { } houseMode)
				return null;

			foreach (HouseModeOptionConfig option in houseMode.Options)
				if (option.ActivateWhileOn.FirstOrDefault(IsOn) is { Length: > 0 } entityId)
					return (option, entityId);

			return null;
		}
	}

	/// <summary>What is forcing the effective mode, or <c>null</c> when the select's own value is the whole story.</summary>
	// Read live, in the order the engine decides in: the ActivateWhileOn overlay wins over the select, so it is
	// asked first. Every kind is reported, not only Away.
	internal ForcedMode? Forced
	{
		get
		{
			if (ActivatedNow is { } activated)
				return new ForcedMode(
					activated.Option.Kind,
					activated.Option.Value?.Trim() ?? "",
					ModeForceSource.WhileEntityOn,
					activated.EntityId,
					_ha.GetState(activated.EntityId).AsUsableState());

			string? claimed;
			lock (_gate)
				claimed = _inactivityActivated;

			if (claimed is not { Length: > 0 })
				return null;

			// The claim holds only while the value the engine wrote is still what the select reads.
			if (!string.Equals(CurrentModeValue, claimed, StringComparison.OrdinalIgnoreCase)
				|| _global.HouseMode?.OptionFor(claimed) is not { } option)
				return null;

			return new ForcedMode(option.Kind, claimed, ModeForceSource.NoMotionTimeout);
		}
	}

	// The option the engine acts on: an ActivateWhileOn override wins over the select's value, and empty
	// ActivateWhileOn lists leave this equal to the select-standing option.
	private HouseModeOptionConfig? EffectiveOption => ActivatedNow?.Option ?? CurrentOption;

	/// <summary>The kind of the effective option; <see cref="ModeKind.Normal"/> when nothing classifies.</summary>
	internal ModeKind ActiveKind => EffectiveOption?.Kind ?? ModeKind.Normal;

	/// <summary>The effective option's own name as written in the document, trimmed; <c>null</c> when nothing classifies.</summary>
	internal string? ActiveModeName =>
		EffectiveOption is { Value: { } value } && value.Trim() is { Length: > 0 } name ? name : null;

	// An unreadable state is "not on", so a vanished sensor stops forcing its mode instead of pinning it.
	private bool IsOn(string entityId) =>
		!string.IsNullOrWhiteSpace(entityId) && (_ha.GetState(entityId)?.IsOn() ?? false);

	/// <summary>Stamps the instants this run starts from. Called under the gate, before anything else runs.</summary>
	// A restart is neither a mode being set nor somebody moving. Stamping both with now restarts the presence grace
	// over a mode chosen hours ago and hands the quiet-time rule a fresh window every time a save rebuilds the
	// engine. Home Assistant's own timestamps outlive both.
	internal void StampStart(DateTimeOffset now)
	{
		_activatedAt = ModeSetAt(now);
		_lastMotionAt = LastMotionAt(now);
	}

	/// <summary>When the mode standing now was chosen, which a mode's temperature is stamped with.</summary>
	internal DateTimeOffset ActivatedAt
	{
		get { lock (_gate) return _activatedAt; }
	}

	/// <summary>When the select last moved, which is when the mode standing now was chosen.</summary>
	// Falls back to the start instant.
	private DateTimeOffset ModeSetAt(DateTimeOffset now) =>
		_global.HouseMode?.Entity is { Length: > 0 } entityId
			? ChangedAt(_ha.GetState(entityId), now) ?? now
			: now;

	/// <summary>The newest edge any watched motion sensor reports, or the start instant when none does.</summary>
	private DateTimeOffset LastMotionAt(DateTimeOffset now)
	{
		DateTimeOffset? newest = null;

		foreach (string sensor in _areaMotionSensors)
			if (ChangedAt(_ha.GetState(sensor), now) is { } stamp && (newest is null || stamp > newest))
				newest = stamp;

		return newest ?? now;
	}

	/// <summary>An entity's <c>last_changed</c> as an instant, never later than <paramref name="now"/>.</summary>
	// Home Assistant publishes UTC; a kindless value lost its label in the JSON reader and is never local time.
	// The clamp covers a Home Assistant host whose clock runs ahead of this one.
	private static DateTimeOffset? ChangedAt(EntityState? state, DateTimeOffset now)
	{
		if (state?.LastChanged is not { } raw)
			return null;

		DateTimeOffset stamp = raw.Kind switch
		{
			DateTimeKind.Utc => new DateTimeOffset(raw, TimeSpan.Zero),
			DateTimeKind.Local => new DateTimeOffset(raw).ToUniversalTime(),
			_ => new DateTimeOffset(DateTime.SpecifyKind(raw, DateTimeKind.Utc), TimeSpan.Zero)
		};

		return stamp > now ? now : stamp;
	}

	/// <summary>Subscribes the select, the reset sensors and the activation sensors, and announces what is forcing the mode.</summary>
	internal void Subscribe()
	{
		if (_global.HouseMode?.Entity is { Length: > 0 } select)
		{
			_logger.LogInformation("Watching house-mode select {EntityId}.", select);
			_subscriptions.Add(_ha.Entity(select)
				.StateChanges()
				.SubscribeSafe(OnSelectChanged, _logger));
		}
	}

	// The select moving, for any reason, restarts the activation clock and republishes house state. Nothing else
	// ever clears a mode, so retention is free.
	private void OnSelectChanged(StateChange change)
	{
		lock (_gate)
		{
			if (_isDisposed())
				return;

			_activatedAt = _scheduler.Now;

			// The select has moved, so whatever it now reads is newer than anything the engine assumed about it.
			// This is also where the echo of the engine's own write lands, and clearing it there costs nothing:
			// the value is the same one, so the republish below says nothing new.
			_assumedMode = null;
			_assumedGeneration++;

			// Anything that moves the select away takes ownership back from the no-motion rule, so a later move
			// onto the same option is somebody else's doing.
			if (_inactivityActivated is { Length: > 0 } claimed
				&& !(change.New?.State).SameName(claimed))
				_inactivityActivated = null;
		}

		// After the stamp above, so the new mode's grace is measured from when it was set.
		ArmGraceExpiryCheck();

		AnnounceForcedMode();
		_raiseChanged();
	}

	internal void SubscribePresenceResets()
	{
		// A reset writes the select, so it stands down with the rest of them.
		if (HouseModeIsHomeAssistants || _global.HouseMode is not { } houseMode)
			return;

		foreach (HouseModeOptionConfig? option in houseMode.Options.Where(o => o.Kind != ModeKind.Normal && o.ResetOnPresence))
		{
			List<string> sensors = PresenceSensorsFor(option);

			if (sensors.Count == 0)
			{
				_logger.LogWarning(
					"Option '{Option}' resets on presence but no sensors resolve (empty list and no area motion sensors); it will never reset on presence.",
					option.Value);
				continue;
			}

			foreach (string sensor in sensors)
			{
				HouseModeOptionConfig captured = option;

				// person.* and device_tracker.* report presence as their state going to "home", never as an on/off
				// edge, so they cannot take the turn-on branch. A binary_sensor arms on its turn-on.
				if (IsPresenceTracker(sensor))
					_subscriptions.Add(_ha.Entity(sensor)
						.StateChanges()
						.Where(IsArrival)
						.SubscribeSafe(_ => OnPresenceReset(captured, sensor), _logger));
				else
					_subscriptions.Add(_ha.Entity(sensor)
						.WhenTurnsOn(_ => OnPresenceReset(captured, sensor), _logger));
			}
		}
	}

	/// <summary>The sensors whose presence resets this option: the ones it lists, or the whole area motion union.</summary>
	// One definition, because the subscriptions and the grace-expiry check must watch the same set.
	private List<string> PresenceSensorsFor(HouseModeOptionConfig option) =>
		option.ResetPresenceSensors.Count > 0 ? option.ResetPresenceSensors : [.. _areaMotionSensors];

	// person.* and device_tracker.* say where somebody is, not whether a room is occupied, so only their arrival
	// counts. A tracker sitting at "home" is the resting state of a phone that never left, and holding the reset
	// open on it would stop the house ever staying away.
	private static bool IsPresenceTracker(string sensor) =>
		sensor.HasDomain(PersonDomain) || sensor.HasDomain(DeviceTrackerDomain);

	// The ActivateWhileOn overlay. The select is never written; EffectiveOption reads these live, so this only
	// republishes house state.
	internal void SubscribeActivationSensors()
	{
		if (HouseModeIsHomeAssistants || _global.HouseMode is not { } houseMode)
			return;

		IEnumerable<string> sensors = houseMode.Options
			.SelectMany(option => option.ActivateWhileOn)
			.Where(sensor => !string.IsNullOrWhiteSpace(sensor))
			.Select(sensor => sensor.Trim())
			.Distinct(StringComparer.OrdinalIgnoreCase);

		foreach (string sensor in sensors)
		{
			_logger.LogInformation("Watching mode-activation sensor {EntityId}.", sensor);
			_subscriptions.Add(_ha.Entity(sensor)
				.StateChanges()
				.SubscribeSafe(_ => OnActivationSensorChanged(), _logger));
		}
	}

	// Announced before the state is republished: nothing writes the select here, so this is the one mode change
	// with no visible cause.
	private void OnActivationSensorChanged()
	{
		AnnounceForcedMode();
		_raiseChanged();
	}

	/// <summary>Writes one line naming what is forcing the house mode, and one when nothing is any more.</summary>
	// Deduplicated on the sentence and never on the entity, so an entity that stays on says it once while a
	// different entity taking over gets its own line. Every kind is announced, not only Away.
	internal void AnnounceForcedMode()
	{
		ForcedMode? forced = Forced;
		string sentence = forced?.Describe() ?? "";

		lock (_gate)
		{
			if (string.Equals(_announcedForce, sentence, StringComparison.Ordinal))
				return;

			_announcedForce = sentence;
		}

		if (forced is null)
		{
			_logger.LogInformation("Nothing is forcing the house mode any more; the select's own value decides again.");
			return;
		}

		// Passed as a property and never concatenated, so a structured sink keeps the sentence whole.
		_logger.LogInformation(
			"{ForcedMode} The house-mode select reads '{Select}' and is being overridden; this is not a presence departure.",
			sentence, CurrentModeValue ?? "(nothing)");
	}

	/// <summary>Movement anywhere in the house: the quiet spell starts again.</summary>
	internal void MarkMotion()
	{
		lock (_gate)
		{
			_lastMotionAt = _scheduler.Now;
			_inactivityLatched = false;
		}
	}

	private bool AnyMotionOn() => _areaMotionSensors.Any(IsOn);

	/// <summary>Switches the select to an option once the whole house has been motion-free for its configured span.</summary>
	// Polled on the tick. First qualifying option in list order wins, one activation per tick. It writes the
	// select, so the option's reset triggers arm through OnSelectChanged as a manual switch would.
	internal void EvaluateInactivityActivation(DateTimeOffset now)
	{
		// Stood down under Home Assistant's authority; ModeStartupReporter has already said so.
		if (HouseModeIsHomeAssistants || _global.HouseMode is not { Entity: { Length: > 0 } select } houseMode)
			return;

		// With nothing watching for movement there is no quiet spell to measure, only a clock nothing ever
		// restarts, which reads as quiet from the first tick. The start-up report warns that the rule can never fire.
		if (_areaMotionSensors.Count == 0)
			return;

		// Motion in progress keeps the clock at now and re-arms, so "no motion for X" counts only quiet time.
		if (AnyMotionOn())
		{
			lock (_gate)
			{
				_lastMotionAt = now;
				_inactivityLatched = false;
			}
			return;
		}

		DateTimeOffset lastMotionAt;
		bool latched;
		lock (_gate)
		{
			lastMotionAt = _lastMotionAt;
			latched = _inactivityLatched;
		}

		// Once per idle spell. The latch clears only when motion resumes; without it the switch repeats every tick
		// until Home Assistant echoes back, and re-fires against a person who switches away while the house is quiet.
		if (latched)
			return;

		foreach (HouseModeOptionConfig option in houseMode.Options
			.Where(candidate => candidate.Kind != ModeKind.Normal && candidate.ActivateAfterNoMotionMinutes is > 0))
		{
			if (_modeSelect.AlreadyShows(option.Value))
				continue;   // already standing on this mode

			if (now - lastMotionAt < TimeSpan.FromMinutes(option.ActivateAfterNoMotionMinutes!.Value))
				continue;

			// A write that never went out sets nothing, so nothing is claimed and the next tick asks again.
			if (!WriteMode(option.Value, entity => _logger.LogInformation(
				"No motion for {Minutes} min; setting {Select} to '{Mode}'.",
				option.ActivateAfterNoMotionMinutes, entity, option.Value)))
				return;

			lock (_gate)
			{
				// So this mode reports as the engine's doing and never as a presence departure. Survives only as
				// long as the select keeps reading it; see OnSelectChanged.
				_inactivityActivated = option.Value.Trim();

				// Movement arriving while the write was out ends the quiet spell that triggered it, and MarkMotion
				// has already cleared the latch. Setting it here would discard that movement and refuse for ever.
				if (_lastMotionAt == lastMotionAt)
					_inactivityLatched = true;
			}

			return;
		}
	}

	/// <summary>The one place the house-mode select is written, so no rule can forget the master switch.</summary>
	// The muzzle covers this as much as it covers a light: the mode decides the leaving sweep, the away scene and
	// the sleep ceiling, so an engine writing it while paused is still driving the house. Nothing is queued, and
	// every rule that reaches here is asked again: the inactivity rule on its next tick, a reset on its next
	// trigger, a period's mode switch at its next boundary.
	internal bool WriteMode(string wanted, Action<string> announce, bool actAtOnce = false)
	{
		if (_isDisposed())
			return false;

		if (KillSwitchActive)
		{
			_logger.LogDebug("The master switch is on, so {Select} is left where it stands.", _modeSelect.Entity);
			return false;
		}

		if (!_modeSelect.Ensure(wanted, announce))
			return false;

		if (!actAtOnce)
			return true;

		lock (_gate)
		{
			_assumedMode = wanted.Trim();
			_assumedGeneration++;
			_lastKnownMode = _assumedMode;
		}

		AnnounceForcedMode();
		_raiseChanged();
		return true;
	}

	/// <summary>Drops an assumed mode the select has not taken, so its own value decides again.</summary>
	// From the tick, which is the first moment a lost write can be told from an echo still on its way. Home
	// Assistant's echo does not reach here: the select moving raises a state change, and OnSelectChanged clears it.
	internal void ExpireAssumedMode()
	{
		string? assumed;
		int generation;
		lock (_gate)
		{
			assumed = _assumedMode;
			generation = _assumedGeneration;
		}

		if (assumed is null || _modeSelect.AlreadyShows(assumed))
			return;

		// A reset or a select change landing since the read above is newer than this verdict.
		lock (_gate)
		{
			if (_assumedGeneration != generation)
				return;

			_assumedMode = null;
		}

		_logger.LogInformation(
			"{Select} did not take '{Mode}'; the house goes back to the mode the select itself reads.",
			_modeSelect.Entity, assumed);

		AnnounceForcedMode();
		_raiseChanged();
	}

	private static bool IsArrival(StateChange change) =>
		string.Equals(change.New?.State, HomeState, StringComparison.OrdinalIgnoreCase)
		&& !string.Equals(change.Old?.State, HomeState, StringComparison.OrdinalIgnoreCase);

	// Edge-triggered on a fresh turn-on or arrival. Resets only when this option is active and the grace window
	// has expired, so walking out the door does not cancel the mode just set.
	private void OnPresenceReset(HouseModeOptionConfig option, string sensor)
	{
		if (_isDisposed() || !ReferenceEquals(CurrentOption, option))
			return;

		DateTimeOffset activatedAt;
		lock (_gate)
			activatedAt = _activatedAt;

		TimeSpan grace = TimeSpan.FromMinutes(Math.Max(0, option.ResetPresenceGraceMinutes));
		if (_scheduler.Now - activatedAt < grace)
		{
			_logger.LogDebug("Presence on {Sensor} within the {Grace}-minute grace of '{Option}'; ignored.",
				sensor, option.ResetPresenceGraceMinutes, option.Value);
			return;
		}

		Reset($"presence on {sensor}");
	}

	/// <summary>Schedules the one look at the reset sensors that a grace running out is owed.</summary>
	// A state-change stream reports a sensor once, when it changes. A sensor that comes on inside the grace and stays
	// on is therefore never reported again, and without this the mode it should have cancelled is held for the whole
	// of the occupant's stay. Armed from the instant the mode was set, so it is one look per mode and not per event.
	internal void ArmGraceExpiryCheck()
	{
		_graceEnds.Disposable = Disposable.Empty;

		if (HouseModeIsHomeAssistants || KillSwitchActive)
			return;

		if (CurrentOption is not { Kind: not ModeKind.Normal, ResetOnPresence: true } option)
			return;

		DateTimeOffset activatedAt;
		lock (_gate)
			activatedAt = _activatedAt;

		TimeSpan grace = TimeSpan.FromMinutes(Math.Max(0, option.ResetPresenceGraceMinutes));
		TimeSpan wait = activatedAt + grace - _scheduler.Now;

		_graceEnds.Disposable = _scheduler.Schedule(
			wait > TimeSpan.Zero ? wait : TimeSpan.Zero,
			() => OnGraceExpired(option));
	}

	/// <summary>Resets when a reset sensor is still reading on at the moment the grace runs out.</summary>
	// Only the on/off sources are read: IsOn answers false for unavailable and unknown, so a sensor that has stopped
	// answering holds nothing. Once the reset lands the select stands on Normal, which is not an option carrying this
	// rule, so the next arming cancels itself and nothing repeats.
	private void OnGraceExpired(HouseModeOptionConfig option)
	{
		if (_isDisposed() || !ReferenceEquals(CurrentOption, option))
			return;

		if (PresenceSensorsFor(option).FirstOrDefault(sensor => !IsPresenceTracker(sensor) && IsOn(sensor))
			is not { Length: > 0 } held)
			return;

		Reset($"{held} still reading presence as the grace ran out");
	}

	/// <summary>Returns the select to the single Normal option, logging which trigger fired.</summary>
	internal void Reset(string trigger)
	{
		// Under Home Assistant's authority nothing here writes the select, resets included.
		if (HouseModeIsHomeAssistants || _global.HouseMode is not { Entity: { Length: > 0 } select } houseMode)
			return;

		// No Normal option means nothing to reset to: a no-op, never a clobber onto a tagged option.
		if (houseMode.NormalOption?.Value is not { Length: > 0 } normal)
		{
			if (System.Threading.Interlocked.Exchange(ref _warnedNoNormal, 1) == 0)
				_logger.LogWarning("A reset fired ({Trigger}) but no Normal option resolves; leaving the mode unchanged (this warns once).", trigger);
			return;
		}

		// The one write the house acts on before the echo. A reset is an arrival, and waiting for the echo refuses the
		// first movement. Every other write waits, so a departure the select never took does not happen at all.
		WriteMode(
			normal,
			entity => _logger.LogInformation("Resetting {Select} to '{Normal}' ({Trigger}).", entity, normal, trigger),
			actAtOnce: true);
	}

	/// <inheritdoc/>
	public void Dispose() => _graceEnds.Dispose();
}
