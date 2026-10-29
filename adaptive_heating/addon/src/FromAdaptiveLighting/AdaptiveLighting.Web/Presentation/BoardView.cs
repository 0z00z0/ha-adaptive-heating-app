using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Web.Presentation;

/// <summary>The stretch of time the board draws, and the arithmetic that turns an instant into a position on it.</summary>
/// <remarks>Both ends are snapped to whole hours, so the window steps once an hour instead of sliding.</remarks>
/// <param name="End">The newest instant, on the hour, in the future; the board shows what is coming.</param>
public sealed record BoardWindow(DateTimeOffset Start, DateTimeOffset End)
{
	public static BoardWindow Around(DateTimeOffset now, TimeSpan back, TimeSpan ahead)
	{
		DateTimeOffset start = FloorToHour(now - back);
		DateTimeOffset end = CeilingToHour(now + ahead);

		// PercentAt divides by the width, so a degenerate window must never leave this method.
		return new BoardWindow(start, end > start ? end : start.AddHours(1));
	}

	public int Hours => (int)Math.Round((End - Start).TotalHours);

	/// <summary>The hour boundaries, first and last inclusive: the axis's labelled ticks.</summary>
	public IReadOnlyList<DateTimeOffset> Ticks
	{
		get
		{
			List<DateTimeOffset> ticks = new(Hours + 1);
			for (DateTimeOffset at = Start; at <= End; at = at.AddHours(1))
				ticks.Add(at);

			return ticks;
		}
	}

	/// <summary>A position as a percentage of the window's width. An instant outside it falls outside 0-100.</summary>
	public double PercentAt(DateTimeOffset at) => (at - Start) / (End - Start) * 100.0;

	public bool Contains(DateTimeOffset at) => at >= Start && at <= End;

	private static DateTimeOffset FloorToHour(DateTimeOffset at) =>
		new(at.Year, at.Month, at.Day, at.Hour, 0, 0, at.Offset);

	private static DateTimeOffset CeilingToHour(DateTimeOffset at)
	{
		DateTimeOffset floored = FloorToHour(at);
		return floored == at ? at : floored.AddHours(1);
	}
}

/// <summary>What a stretch of a room's past is drawn as. Only states worth a mark have one.</summary>
public enum LaneBlockKind
{
	/// <summary>The add-on was holding the room at its mode and period, in the temperature it set.</summary>
	Auto,

	/// <summary>A change at the thermostat stood.</summary>
	Hand,

	/// <summary>A guest mode or a watched entity held the room as it stood.</summary>
	Held
}

/// <param name="WidthPct">How wide it is, never narrower than a stretch a person can see.</param>
/// <param name="Temperature">The temperature the room was held at, for an automatic stretch; <c>null</c> otherwise.</param>
public sealed record LaneBlock(LaneBlockKind Kind, double LeftPct, double WidthPct, double? Temperature);

/// <summary>A dotted mark in a lane's future: when the room's armed timer will act, and what it will do.</summary>
public sealed record LaneMark(double LeftPct, string Label);

/// <summary>One room's row on the board: what it is doing now, what it did, and what happens next.</summary>
/// <param name="Key">The stable identity: the area id, or the display name when there is none.</param>
public sealed record BoardLane(
	string Key,
	string Name,
	string? AreaId,
	AreaSnapshot Latest,
	IReadOnlyList<LaneBlock> Blocks,
	LaneMark? Next)
{
	/// <summary>Whether this room has nothing to say: no past worth drawing, nothing armed, nothing wrong.</summary>
	public bool IsQuiet => Blocks.Count == 0 && Next is null && !BoardView.IsException(Latest);
}

/// <param name="Temperature">The period's temperature, which colours the band.</param>
public sealed record BandSegment(string Name, double LeftPct, double WidthPct, double? Temperature);

/// <summary>Everything the board decides, as pure functions. Nothing here touches the configuration document.</summary>
public static class BoardView
{
	public static readonly TimeSpan LookBack = TimeSpan.FromHours(4);

	/// <summary>How much future the board shows: enough to hold the next period boundary and a hold running out.</summary>
	public static readonly TimeSpan LookAhead = TimeSpan.FromHours(2);

	public const int LaneBudget = 6;

	/// <summary>How many log lines the board carries before handing over to the Activity page.</summary>
	public const int LogPreview = 12;

	/// <summary>The narrowest a block may be drawn, so a short stretch is still a mark and not a hairline.</summary>
	private const double MinBlockPct = 0.4;

	/// <summary>The narrowest band segment that gets its name written in it.</summary>
	private const double MinLabelledBandPct = 9;

	// ===================== the lanes =====================

	/// <summary>One room's past, as blocks clipped to the board's window.</summary>
	/// <remarks>
	///     Each report covers the stretch to the next one and the newest runs to <paramref name="now"/>. Adjacent
	///     stretches merge before they become percentages, so the minimum width applies to the merged whole.
	/// </remarks>
	/// <param name="history">The room's reports in arrival order.</param>
	public static IReadOnlyList<LaneBlock> Blocks(IEnumerable<AreaSnapshot> history, BoardWindow window, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(history);
		ArgumentNullException.ThrowIfNull(window);

		List<AreaSnapshot> ordered = [.. history.OrderBy(snapshot => snapshot.Timestamp)];
		List<(LaneBlockKind Kind, double? Temperature, DateTimeOffset From, DateTimeOffset To)> spans = [];

		for (int index = 0; index < ordered.Count; index++)
		{
			AreaSnapshot snapshot = ordered[index];

			if (KindOf(snapshot.State) is not { } kind)
				continue;

			DateTimeOffset from = snapshot.Timestamp;
			DateTimeOffset to = index + 1 < ordered.Count ? ordered[index + 1].Timestamp : now;
			if (to < from)
				to = from;

			if (to <= window.Start || from >= window.End)
				continue;

			DateTimeOffset clippedFrom = from < window.Start ? window.Start : from;
			DateTimeOffset clippedTo = to > window.End ? window.End : to;
			double? temperature = kind == LaneBlockKind.Auto ? snapshot.Target : null;

			if (spans.Count > 0
				&& spans[^1].Kind == kind
				&& Nullable.Equals(spans[^1].Temperature, temperature)
				&& spans[^1].To >= clippedFrom)
			{
				spans[^1] = spans[^1] with { To = clippedTo };
				continue;
			}

			spans.Add((kind, temperature, clippedFrom, clippedTo));
		}

		return
		[
			.. spans.Select(span =>
			{
				double left = Math.Clamp(window.PercentAt(span.From), 0, 100 - MinBlockPct);
				double width = Math.Max(MinBlockPct, window.PercentAt(span.To) - left);

				return new LaneBlock(span.Kind, left, Math.Min(width, 100 - left), span.Temperature);
			})
		];
	}

	/// <summary>The one dotted mark ahead of the now-line: when this room's armed timer fires, and what it does.</summary>
	/// <remarks>A passed deadline and an unarmed state both get no mark.</remarks>
	public static LaneMark? NextMark(AreaSnapshot snapshot, BoardWindow window, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(window);

		if (snapshot.NextChangeAt is not { } at || at < now || !window.Contains(at))
			return null;

		return NextWord(snapshot.State) is { } word
			? new LaneMark(window.PercentAt(at), $"{Clock(at)} {word}")
			: null;
	}

	/// <summary>
	///     Splits the lanes into the ones that earn a row and the ones that become a chip on the quiet shelf.
	///     Below <see cref="LaneBudget"/> nothing is folded, whatever the rooms are doing.
	/// </summary>
	public static (IReadOnlyList<BoardLane> Busy, IReadOnlyList<BoardLane> Quiet) Partition(IReadOnlyList<BoardLane> lanes)
	{
		ArgumentNullException.ThrowIfNull(lanes);

		if (lanes.Count <= LaneBudget)
			return (lanes, []);

		return ([.. lanes.Where(lane => !lane.IsQuiet)], [.. lanes.Where(lane => lane.IsQuiet)]);
	}

	// ===================== the exception tray =====================

	/// <summary>A state where the room is not following its mode and schedule.</summary>
	public static bool IsException(AreaState state) =>
		state is AreaState.OverriddenOn or AreaState.SceneHold or AreaState.NotReporting;

	public static bool IsException(AreaSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		return IsException(snapshot.State) || IsPausedByAnEntity(snapshot);
	}

	/// <summary>Whether something the room watches has it heated as in Away.</summary>
	internal static bool IsPausedByAnEntity(AreaSnapshot snapshot) =>
		snapshot is { State: AreaState.Away, AutoOnBlockedBy: AutoOnBlock.EntityOn };

	/// <summary>The rooms the tray carries, alphabetical, so the tray does not reshuffle itself on every tick.</summary>
	public static IReadOnlyList<AreaSnapshot> Exceptions(IEnumerable<AreaSnapshot> snapshots)
	{
		ArgumentNullException.ThrowIfNull(snapshots);

		return [.. snapshots.Where(IsException).OrderBy(snapshot => snapshot.AreaName, StringComparer.CurrentCulture)];
	}

	/// <summary>What a tray chip says after the room's name.</summary>
	public static string ExceptionLine(AreaSnapshot snapshot, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		if (IsPausedByAnEntity(snapshot))
		{
			return snapshot.AutoOnBlockingEntity is { Length: > 0 } blocking
				? $"paused while {blocking} is on"
				: "paused while something here is on";
		}

		return snapshot.State switch
		{
			AreaState.OverriddenOn => snapshot.NextChangeAt is { } resumes
				? $"set by hand — until {Clock(resumes)}"
				: "set by hand — until resumed",

			AreaState.SceneHold => "held",

			_ => StateGlyph.For(snapshot).Word
		};
	}

	public static string QuietRoomsLine(int rooms, int exceptions)
	{
		int quiet = Math.Max(0, rooms - exceptions);

		if (quiet == 0)
			return exceptions == 1 ? "The only room on" : "Every room on is here";

		string subject = exceptions == 0
			? quiet switch
			{
				1 => "The one room",
				2 => "Both rooms",
				_ => $"All {quiet} rooms"
			}
			: quiet == 1 ? "The other room" : $"The other {quiet}";

		return $"{subject} on schedule";
	}

	/// <summary>What the summary shows of what the record holds, and what it holds back for the Activity page.</summary>
	// reachable is what the Activity page would draw; the held-back count is measured against it.
	public static string LogFoot(int held, int reachable, int kept, int shown, int capacity)
	{
		if (held <= 0)
			return "nothing recorded yet";

		int hidden = Math.Max(0, reachable - kept);

		string lead = shown >= LogPreview
			? $"newest {shown} of {Count(kept, "report")}"
			: Count(kept, "report");

		// When the filter has taken everything, the hidden count is the answer.
		if (hidden > 0)
		{
			lead = kept == 0
				? $"{hidden} everyday on Activity"
				: $"{lead} · {hidden} everyday on Activity";
		}

		return held >= capacity
			? $"{lead} · last {capacity} kept"
			: lead;
	}

	private static string Count(int count, string singular) => $"{count} {(count == 1 ? singular : singular + "s")}";

	// ===================== the schedule band =====================

	/// <summary>The day's periods as segments of the board's width, as the engine's own table places them.</summary>
	public static IReadOnlyList<BandSegment> Band(CircadianCalculator calculator, BoardWindow window)
	{
		ArgumentNullException.ThrowIfNull(calculator);
		ArgumentNullException.ThrowIfNull(window);

		List<BandSegment> segments = [];

		foreach (PeriodSpan span in calculator.PeriodsAcross(window.Start, window.End))
		{
			double left = Math.Max(0, window.PercentAt(span.From));
			double right = Math.Min(100, window.PercentAt(span.To));

			if (right - left <= 0)
				continue;

			segments.Add(new BandSegment(span.Period.Name, left, right - left, span.Period.Temperature));
		}

		return segments;
	}

	public static bool IsLabelled(BandSegment segment)
	{
		ArgumentNullException.ThrowIfNull(segment);

		return segment.WidthPct >= MinLabelledBandPct;
	}

	// ===================== words =====================

	/// <summary>A wall-clock time, in the reader's culture: the format every surface in this UI uses.</summary>
	public static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);

	/// <summary>What a state is drawn as, or <c>null</c> for the quiet states, which draw nothing.</summary>
	private static LaneBlockKind? KindOf(AreaState state) => state switch
	{
		AreaState.AutoActive or AreaState.WarmingUp => LaneBlockKind.Auto,
		AreaState.OverriddenOn => LaneBlockKind.Hand,
		AreaState.SceneHold => LaneBlockKind.Held,
		_ => null
	};

	/// <summary>What the armed timer of a state will do when it fires, or <c>null</c> when the state arms none.</summary>
	private static string? NextWord(AreaState state) => state switch
	{
		AreaState.OverriddenOn => "auto resumes",
		_ => null
	};
}
