using Adaptive.Settings;
using AdaptiveLighting.Configuration;

namespace Adaptive.Engine;

/// <summary>Why a configured period could not be placed in the circadian table.</summary>
public enum PeriodDropReason
{
	/// <summary>The <c>Start</c> string is neither a clock time nor a sun anchor.</summary>
	Unparseable,

	/// <summary>A sun-anchored <c>Start</c> with no sun time to resolve against today: polar night, or no sun entity.</summary>
	Unresolvable
}

/// <summary>A period the calculator had to leave out of the table, and why.</summary>
// Surfaced so a vanished period is a logged warning and never a silent hole the table wraps over.
public sealed record DroppedPeriod(string PeriodName, string Start, PeriodDropReason Reason);

/// <summary>A stretch of time one period holds, bounded by the instants either side of it.</summary>
public sealed record PeriodSpan(TimePeriodConfig Period, DateTimeOffset From, DateTimeOffset To);

/// <summary>Turns the configured period table into the period in force at a given instant.</summary>
// Pure with respect to time and I/O: the instant is an argument, the day's sun times arrive through a delegate,
// and nothing here logs. Every member is safe to call from any thread; the drop record is the only mutable state
// and is guarded here, so no caller has to hold a lock of its own.
// Under PeriodAuthority.HomeAssistant a dropdown decides which period is in force, and OverriddenPeriod is the
// one point both public answers go through.
public sealed class CircadianCalculator
{
	private readonly IReadOnlyList<TimePeriodConfig> _periods;
	private readonly GlobalConfig _global;
	private readonly Func<SunTimes> _sunTimes;

	// Absent except under HomeAssistant period authority, never installed and answering null.
	private readonly Func<string?>? _periodOverride;

	private readonly TimeZoneInfo _zone;

	// Parsed once: only the sun-anchor resolution depends on the day, so a tick is Resolve() plus a sort.
	private readonly IReadOnlyList<(PeriodStart Start, TimePeriodConfig Period)> _parsedStarts;

	// Dedup for the drop reporting, or an unplaceable boundary reports on all 1440 of the day's ticks.
	private readonly HashSet<DroppedPeriod> _dropped = [];

	// _dropped is the only mutable state here, and NextBoundary reaches it from BoundaryTimer's own thread.
	private readonly Lock _gate = new();

	/// <summary>Raised the first time a period is dropped during evaluation, deduplicated.</summary>
	// Parse failures are known before any subscriber can attach, so they arrive through DroppedPeriods instead.
	public event Action<DroppedPeriod>? PeriodDropped;

	/// <summary>The drops recorded so far, as a snapshot, so a reader cannot enumerate while an evaluation adds.</summary>
	public IReadOnlyCollection<DroppedPeriod> DroppedPeriods
	{
		get
		{
			lock (_gate)
				return [.. _dropped];
		}
	}

	// periodOverride comes from PeriodSelectReader.ReadPeriod alone, and null resolves from the schedule. zone is the
	// household's: a boundary is a wall clock and the instants handed in are not.
	/// <summary>Creates a calculator over <paramref name="periods"/>, whose order is irrelevant.</summary>
	public CircadianCalculator(
		IReadOnlyList<TimePeriodConfig> periods,
		GlobalConfig global,
		Func<SunTimes> sunTimes,
		Func<string?>? periodOverride = null,
		TimeZoneInfo? zone = null)
	{
		_periods = periods ?? throw new ArgumentNullException(nameof(periods));
		_global = global ?? throw new ArgumentNullException(nameof(global));
		_sunTimes = sunTimes ?? throw new ArgumentNullException(nameof(sunTimes));
		_periodOverride = periodOverride;
		_zone = zone ?? TimeZoneInfo.Local;

		List<(PeriodStart Start, TimePeriodConfig Period)> parsed = new(_periods.Count);
		foreach (TimePeriodConfig period in _periods)
			if (PeriodStart.TryParse(period.Start, out PeriodStart? start))
				parsed.Add((start!, period));
			else
				// No subscriber can exist yet, so record without raising and let DroppedPeriods carry it.
				RecordDrop(new DroppedPeriod(period.Name, period.Start, PeriodDropReason.Unparseable), raiseEvent: false);

		_parsedStarts = parsed;
	}

	// raiseEvent is false only for the constructor's parse failures.
	private void RecordDrop(DroppedPeriod drop, bool raiseEvent)
	{
		bool first;

		lock (_gate)
			first = _dropped.Add(drop);

		// Outside the lock: a subscriber logs, and holding this one across it would be a second lock order.
		if (first && raiseEvent)
			PeriodDropped?.Invoke(drop);
	}

	/// <summary>The period a reference names, or <c>null</c> when this table has none.</summary>
	public TimePeriodConfig? PeriodWithKey(string? periodKey) =>
		periodKey is { Length: > 0 } ? _periods.ByKey(periodKey) : null;

	/// <summary>The key of the period active at <paramref name="now"/>, or <c>null</c> when none can be placed.</summary>
	public string? ActivePeriodId(DateTimeOffset now)
	{
		if (OverriddenPeriod() is { } forced)
			return forced.Key;

		return KeyAt(Placed(), now);
	}

	/// <summary>The periods in force from <paramref name="from"/> to <paramref name="to"/>, in order, clipped to that stretch.</summary>
	/// <remarks>
	///     The same table every other answer here comes out of, laid out over a stretch instead of at an instant, so
	///     a surface drawing the schedule cannot draw one the engine is not running. Under an override there is no
	///     boundary at all: the named period holds the whole stretch.
	/// </remarks>
	// Boundaries are placed per local day for the day before the stretch through the day after, so one opening
	// inside yesterday's last period and one reaching past midnight both come out whole. One day's sun times serve
	// them all.
	public IReadOnlyList<PeriodSpan> PeriodsAcross(DateTimeOffset from, DateTimeOffset to)
	{
		if (to <= from)
			return [];

		if (OverriddenPeriod() is { } forced)
			return [new PeriodSpan(forced, from, to)];

		SunTimes sunTimes = _sunTimes();
		List<(DateTimeOffset At, TimePeriodConfig Period)> boundaries = [];

		for (DateOnly day = from.DayIn(_zone).AddDays(-1); day <= to.DayIn(_zone).AddDays(1); day = day.AddDays(1))
		{
			foreach ((PeriodStart start, TimePeriodConfig period) in _parsedStarts)
				if (BoundaryOf(start, period, sunTimes) is { } resolved)
					boundaries.Add((InstantOf(day, resolved), period));
		}

		boundaries.Sort((left, right) => left.At.CompareTo(right.At));

		List<PeriodSpan> spans = new(boundaries.Count);

		for (int index = 0; index < boundaries.Count; index++)
		{
			DateTimeOffset opens = boundaries[index].At;

			// The last boundary runs to the end of the stretch: there is no later one to hand over to.
			DateTimeOffset closes = index + 1 < boundaries.Count ? boundaries[index + 1].At : to;

			DateTimeOffset clippedFrom = opens < from ? from : opens;
			DateTimeOffset clippedTo = closes > to ? to : closes;

			if (clippedTo <= clippedFrom)
				continue;

			spans.Add(new PeriodSpan(boundaries[index].Period, clippedFrom, clippedTo));
		}

		return spans;
	}

	/// <summary>The instant the next boundary after <paramref name="now"/> falls on, wrapping to the next local day.</summary>
	// The moment a caller should ask its ordinary questions again; the answer it gets then still comes from
	// ActivePeriodId.
	public DateTimeOffset? NextBoundary(DateTimeOffset now)
	{
		TimeOnly timeOfDay = now.TimeIn(_zone);
		DateOnly today = now.DayIn(_zone);

		// Today's boundaries still ahead, then tomorrow's first.
		foreach ((TimeOnly start, TimePeriodConfig _) in Placed())
			if (start > timeOfDay)
				return InstantOf(today, start);

		DateOnly tomorrow = today.AddDays(1);
		List<(TimeOnly Start, TimePeriodConfig Period)> wrapped = Placed();

		return wrapped.Count == 0 ? null : InstantOf(tomorrow, wrapped[0].Start);
	}

	/// <summary>A household wall clock on a household day, as the instant it happens at.</summary>
	// A wall clock the spring-forward gap swallows never happens, so the boundary arrives at the first minute
	// that does; an ambiguous autumn one takes ConvertTimeToUtc's standard-time answer, the rule the rest of the
	// table resolves under.
	private DateTimeOffset InstantOf(DateOnly day, TimeOnly boundary)
	{
		DateTime local = day.ToDateTime(boundary, DateTimeKind.Unspecified);

		while (_zone.IsInvalidTime(local))
			local = local.AddMinutes(1);

		return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, _zone));
	}

	private string? KeyAt(List<(TimeOnly Start, TimePeriodConfig Period)> boundaries, DateTimeOffset now) =>
		boundaries.Count == 0
			? null
			: boundaries[ActiveIndex(boundaries, now.TimeIn(_zone))].Period.Key;

	// The single point every consumer of an override goes through. An unmatched id falls through to the schedule,
	// which the validator and PeriodSelectReader already report, and taking a room dark over a stale mapping
	// would be worse.
	private TimePeriodConfig? OverriddenPeriod() => PeriodWithKey(_periodOverride?.Invoke());

	private List<(TimeOnly Start, TimePeriodConfig Period)> Placed()
	{
		SunTimes sunTimes = _sunTimes();
		List<(TimeOnly Start, TimePeriodConfig Period)> boundaries = new(_parsedStarts.Count);

		foreach ((PeriodStart start, TimePeriodConfig period) in _parsedStarts)
			if (BoundaryOf(start, period, sunTimes) is { } resolved)
				boundaries.Add((resolved, period));

		boundaries.Sort((left, right) => left.Start.CompareTo(right.Start));
		return boundaries;
	}

	// The one place a Start becomes a boundary: resolved against the day's sun times, and reported when it cannot be.
	private TimeOnly? BoundaryOf(
		PeriodStart start,
		TimePeriodConfig period,
		SunTimes sunTimes)
	{
		// A sun-anchored boundary the sun entity cannot place is dropped, never guessed at. The table still covers
		// the day by wrapping, so the drop has to be reported or the wrap is silent.
		if (start.Resolve(sunTimes) is not { } resolved)
		{
			RecordDrop(new DroppedPeriod(period.Name, period.Start, PeriodDropReason.Unresolvable), raiseEvent: true);
			return null;
		}

		return resolved;
	}

	// The last boundary at or before timeOfDay, wrapping to yesterday's last period.
	private static int ActiveIndex(List<(TimeOnly Start, TimePeriodConfig Period)> boundaries, TimeOnly timeOfDay)
	{
		int index = -1;
		for (int i = 0; i < boundaries.Count; i++)
			if (boundaries[i].Start <= timeOfDay)
				index = i;

		return index < 0 ? boundaries.Count - 1 : index;
	}
}
