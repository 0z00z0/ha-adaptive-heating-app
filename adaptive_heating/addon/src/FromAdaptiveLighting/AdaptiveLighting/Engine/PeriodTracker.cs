using Adaptive.Settings;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace Adaptive.Engine;

/// <summary>The period half of the mode brain: entry, the note that survives a restart and the period select mirror.</summary>
// The gate is ModeMonitor's own, shared with HouseModeRules: reading which period is in force and claiming the
// transition must be one step, or a period-select flip arriving on Home Assistant's thread acts on it twice.
// Nothing that calls Home Assistant runs under it.
internal sealed class PeriodTracker
{
	private readonly ILogger _logger;
	private readonly IReadOnlyList<TimePeriodConfig> _periods;
	private readonly CircadianCalculator _circadian;
	private readonly HouseModeRules _modes;
	private readonly Func<bool> _isDisposed;
	private readonly Action _raiseChanged;
	private readonly object _gate;

	// Where the period the engine is running in is written down, so the next start can tell whether a boundary
	// went by while it was stopped. Null reads as unknown.
	private readonly ILastPeriodStore? _lastPeriod;

	// Null when no period select is configured. Which direction it grants is its own to say.
	private readonly PeriodSelectReader? _periodSelect;

	// The period select as an output. OptionForPeriod is non-null on the one authority that permits a write.
	private readonly SelectMirror _periodMirror;

	// This engine replaced a running one on a save. The note on disk cannot tell that from a cold start, and a save
	// is not a boundary that went by.
	private readonly bool _afterSave;

	private string? _previousPeriodId;

	// Armed by the start, spent on the first tick that can read the select. Held open until the select answers,
	// because an input_select can be unavailable for a while after a Home Assistant restart.
	private bool _startPeriodModePending;

	// What the previous run left on disk, read once at the start. Null is unknown, which differs from knowing a
	// boundary was crossed.
	private string? _periodAtLastRun;

	// What this run believes is on disk now, so the file is written on a period change and not on every tick.
	private string? _persistedPeriodId;

	// Bounds the mirror log line only. The call itself compares against what the select actually reads, so a
	// select that never echoes is asked again.
	private string? _mirroredPeriodOption;

	internal PeriodTracker(
		IHaContext ha,
		ILogger logger,
		IReadOnlyList<TimePeriodConfig> periods,
		CircadianCalculator circadian,
		ILastPeriodStore? lastPeriod,
		PeriodSelectReader? periodSelect,
		HouseModeRules modes,
		object gate,
		Func<bool> isDisposed,
		Action raiseChanged,
		bool afterSave)
	{
		_logger = logger;
		_periods = periods;
		_circadian = circadian;
		_lastPeriod = lastPeriod;
		_periodSelect = periodSelect;
		_modes = modes;
		_gate = gate;
		_isDisposed = isDisposed;
		_raiseChanged = raiseChanged;
		_afterSave = afterSave;

		// OptionForPeriod is non-null on the one authority that permits a write; PeriodSelectReader is where that
		// branch is decided, and it is decided once.
		_periodMirror = new SelectMirror(ha, periodSelect?.Entity, periodSelect?.OptionForPeriod is not null);
	}

	/// <summary>Reads the note, seeds the latch and records the period this run comes up inside. Called under the gate.</summary>
	// A save rebuilds the monitor, and an edit to the schedule can put a different period in force than the note
	// names. That reads as a boundary the engine slept through, which it is not: the engine was running the whole
	// time. Only a start from nothing may spend the note.
	internal string? BeginRun(DateTimeOffset now)
	{
		_startPeriodModePending = !_afterSave;

		// Read once: the answer is about the run that ended, and a later read finds what this run wrote over it.
		// A file read under the gate is safe only here, before the subscriptions and the tick exist.
		_periodAtLastRun = ReadPeriodAtLastRun();
		_persistedPeriodId = _periodAtLastRun;

		_previousPeriodId = _circadian.ActivePeriodId(now);

		return _previousPeriodId;
	}

	/// <summary>One evaluation of the clock's news: period entry, the across-restart catch-up, the note and the mirror.</summary>
	// Under the gate, because reading the period and claiming the transition must be one step: a period-select flip
	// runs this from Home Assistant's thread, and a transition read but not claimed is acted on twice. The read is
	// inside the gate too; with only the claim under the lock, a thread holding a stale name fires a backwards
	// entry and regresses _previousPeriodId. The read is a state-cache lookup plus a sort and cannot re-enter.
	internal void Tick(DateTimeOffset now, HouseModeOptionConfig? activeOption, bool modeIsReadable)
	{
		// Before the lock: this consults Home Assistant. A period select that cannot be read is no period change.
		// Under Home Assistant authority the period falls through to the clock while the helper is unavailable, and
		// that answer is indistinguishable from a real one: a household holding "day" at 23:30 would have night's
		// SetsMode latch the house asleep, and nothing puts it back when the helper returns.
		bool overrideIsBlind = _periodSelect is { ReadPeriod: not null } reader && reader.CurrentValue() is null;

		string? currentPeriodId;
		bool entered;
		bool applyOnStart;

		lock (_gate)
		{
			currentPeriodId = _circadian.ActivePeriodId(now);

			entered = !overrideIsBlind
				&& currentPeriodId is { Length: > 0 }
				&& _previousPeriodId is { Length: > 0 }
				&& !string.Equals(currentPeriodId, _previousPeriodId, StringComparison.OrdinalIgnoreCase);

			applyOnStart = !entered
				&& !overrideIsBlind
				&& _startPeriodModePending
				&& currentPeriodId is { Length: > 0 }
				&& modeIsReadable;

			// A blind read is no evidence about which period is running, and recording the clock's guess would
			// make the helper's recovery look like a fresh entry.
			if (!overrideIsBlind)
				_previousPeriodId = currentPeriodId;

			// The one restart chance is spent by an entry or by using it. A tick that could read neither the
			// period nor the select leaves it armed.
			if (entered || applyOnStart)
				_startPeriodModePending = false;
		}

		if (entered)
			OnPeriodEntered(currentPeriodId!, activeOption);
		else if (applyOnStart)
			ApplyPeriodModeOnStart(currentPeriodId!);

		// After the decision above, never before it. The note is the only evidence that a boundary went by while
		// the engine was down, and writing first erases it if the process dies between the two.
		RememberPeriod(currentPeriodId);

		// The period select as an output, reading the same answer the two rules above did. No-op unless this
		// application owns the select.
		MirrorPeriodSelect(currentPeriodId);
	}

	private static bool BoundaryWentByWhileDown(string? previousRun, string periodKey) =>
		previousRun is { Length: > 0 } && !string.Equals(previousRun, periodKey, StringComparison.OrdinalIgnoreCase);

	private TimePeriodConfig? PeriodWithKey(string periodKey) => _periods.ByKey(periodKey);

	private string DisplayName(string? periodKey) => DisplayName(_periods, periodKey);

	// Every log line names the period a person would recognise, never the id the engine resolved by.
	internal static string DisplayName(IReadOnlyList<TimePeriodConfig> periods, string? periodKey) =>
		periodKey is { Length: > 0 } && periods.ByKey(periodKey)?.Name is { Length: > 0 } name ? name : periodKey ?? "";

	private void OnPeriodEntered(string periodKey, HouseModeOptionConfig? activeOption)
	{
		TimePeriodConfig? period = PeriodWithKey(periodKey);

		bool resets = activeOption is { Kind: not ModeKind.Normal, ResetOnPeriodStartId: { Length: > 0 } resetPeriod }
			&& resetPeriod.SameName(periodKey);

		// Once, at entry, so a human override mid-period stands. A boundary the engine was not running for never
		// reaches here; ApplyPeriodModeOnStart handles that from the note on disk.
		if (period?.SetsModeId is { Length: > 0 } setsMode
			&& _modes.OptionValueFor(setsMode) is { Length: > 0 } wanted)
		{
			_modes.WriteMode(wanted, entity => _logger.LogInformation(
				"Period '{Period}' started; setting {Select} to '{Mode}'.", DisplayName(periodKey), entity, wanted));

			// One boundary is one instruction. A reset landing after this writes the same select a second time,
			// the later write wins, and both log lines claim to have set the mode.
			if (resets)
				_logger.LogInformation(
					"'{Option}' also resets when period '{Period}' starts, but that period sets the mode itself, so the reset is skipped.",
					activeOption!.Value, DisplayName(periodKey));

			return;
		}

		if (resets)
			_modes.Reset($"period '{DisplayName(periodKey)}' started");
	}

	/// <summary>Applies the current period's <see cref="TimePeriodConfig.SetsModeId"/> once, on the first tick after the start.</summary>
	// The question is whether a boundary went by, never whether the engine restarted: restarting inside the same
	// period is no event, while restarting across a boundary is the event the schedule exists to act on. A null
	// _periodAtLastRun is unknown and does nothing, because guessing the other way overwrites a mode on no
	// evidence, on a path a corrupt file could trigger every start. Kept separate from OnPeriodEntered because
	// that path also fires the period-start reset, which would cancel a retained Away or Guest mode.
	private void ApplyPeriodModeOnStart(string periodKey)
	{
		string? previousRun;
		lock (_gate)
			previousRun = _periodAtLastRun;

		if (previousRun is not { Length: > 0 })
		{
			_logger.LogDebug(
				"Started inside period '{Period}', but there is no note of which period the last run ended in; "
				+ "assuming nothing and leaving the house mode alone.",
				DisplayName(periodKey));
			return;
		}

		if (!BoundaryWentByWhileDown(previousRun, periodKey))
			return;   // the same period the last run ended in: no boundary went by, so nothing to apply

		if (_modes.HouseModeIsHomeAssistants)
			return;

		TimePeriodConfig? period = PeriodWithKey(periodKey);

		if (period?.SetsModeId is not { Length: > 0 } setsMode
			|| _modes.OptionValueFor(setsMode) is not { Length: > 0 } wanted)
			return;

		_modes.WriteMode(wanted, entity => _logger.LogInformation(
			"Period '{Period}' began while the engine was stopped (it was last running in '{Previous}'); setting {Select} to '{Mode}'.",
			DisplayName(periodKey), DisplayName(previousRun), entity, wanted));
	}

	/// <summary>Points the period select at the period the engine's own schedule resolved.</summary>
	// A no-op under PeriodAuthority.HomeAssistant, where the reader hands out no OptionForPeriod, so the two
	// directions are exclusive by construction and never by a flag this method could get wrong. Triggered by
	// comparison, never by memory: a select moved by hand, or one back from a Home Assistant restart on the wrong
	// option, is put right, where an already-asked latch would make both permanent. The log line is bounded
	// separately, because an option the select does not offer is rejected and correctly retried on every tick.
	internal void MirrorPeriodSelect(string? periodKey)
	{
		if (_isDisposed() || _periodSelect?.OptionForPeriod is not { } optionFor || periodKey is not { Length: > 0 })
			return;

		if (optionFor(periodKey) is not { Length: > 0 } wanted)
			return;   // no row maps this period; the validator has already said so if it matters

		_periodMirror.Ensure(wanted, entity =>
		{
			// Info the first time a value is asked for, Debug on every retry: an option the select does not offer
			// is rejected and correctly re-asked on every tick, and that must not fill the log.
			bool firstTime;
			lock (_gate)
			{
				firstTime = !string.Equals(_mirroredPeriodOption, wanted, StringComparison.OrdinalIgnoreCase);
				_mirroredPeriodOption = wanted;
			}

			if (firstTime)
				_logger.LogInformation("Period '{Period}' is in force; setting {Select} to '{Option}'.",
					DisplayName(periodKey), entity, wanted);
			else
				_logger.LogDebug("Period select {Select} still does not read '{Option}'; asking again.", entity, wanted);
		});
	}

	/// <summary>Reads the previous run's period, treating any failure as unknown.</summary>
	// LastPeriodStore promises never to throw, and this catches anyway: the store is an interface a host
	// supplies, and this runs inside the start, where a throw takes the engine down. A note written before periods
	// had ids holds a name, and without translating it a start compares a name against a key and reads a
	// boundary crossing that never happened.
	private string? ReadPeriodAtLastRun()
	{
		if (_lastPeriod is null)
			return null;

		try
		{
			if (_lastPeriod.Load() is not { Length: > 0 } stored)
				return null;

			if (PeriodWithKey(stored) is not null)
				return stored;

			return _periods.ByName(stored)?.Key ?? stored;
		}
		catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
		{
			_logger.LogWarning(exception, "Could not read which period the last run ended in; assuming nothing.");
			return null;
		}
	}

	/// <summary>Writes the period now current, when it has changed since the last write.</summary>
	// Only on a change: a handful of writes a day, and the file is read once per process. A null period, from a
	// table with no placeable boundary, is left unwritten, or a good note is replaced by a worse one.
	private void RememberPeriod(string? periodKey)
	{
		if (_lastPeriod is null || periodKey is not { Length: > 0 })
			return;

		lock (_gate)
		{
			if (string.Equals(_persistedPeriodId, periodKey, StringComparison.OrdinalIgnoreCase))
				return;

			// Recorded as attempted even when the write fails, or a persistent fault warns once a minute.
			_persistedPeriodId = periodKey;
		}

		try
		{
			_lastPeriod.TrySave(periodKey);
		}
		catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
		{
			// Losing the note costs one re-application after the next restart, never the tick it is written from.
			_logger.LogWarning(exception, "Could not record that the engine is now in period '{Period}'.", DisplayName(periodKey));
		}
	}
}
