namespace AdaptiveHeating.AddOn.Reporting;

/// <summary>One line of the activity record: a report, and where it fell in the run.</summary>
/// <param name="Sequence">
///     Counting from one, dense and never reused. Two rooms can be reported on the same instant, so the report's own
///     moment alone does not order the record.
/// </param>
internal sealed record ActivityEntry(long Sequence, HeatingReport Report)
{
	public DateTimeOffset At => Report.At;

	/// <summary>The room's id, which a filter keys on so a rename does not split one room in two.</summary>
	public string? RoomId => Report.RoomId;
}

/// <summary>The record as one consistent read: the entries, and the sequence reached at that same instant.</summary>
/// <remarks>
///     Read apart, a report landing between the two counts as shown while being absent from the list it was shown in,
///     and stays invisible until the next report arrives.
/// </remarks>
internal sealed record ActivityTimeline(IReadOnlyList<ActivityEntry> Entries, long Newest);

/// <summary>What the add-on has recently done, kept in order and bounded.</summary>
/// <remarks>
///     Taken from the lighting engine's <c>ActivityLog</c>, read on 2026-09-25. What changed is the payload: one
///     report kind in place of that engine's area snapshot and house notice, so there is one <c>Record</c> rather
///     than two. The two copies drift, and a fault found in either is carried across by hand.
/// </remarks>
internal sealed class ActivityRecord
{
	/// <summary>How many reports the record keeps before the oldest falls off, a day or two for a real cabin.</summary>
	public const int Capacity = 500;

	private readonly IActivityJournal? _journal;
	private readonly Lock _gate = new();
	private readonly Queue<ActivityEntry> _entries = new(Capacity);

	private long _sequence;

	/// <summary>Seeds the record from the journal, so a restart does not start it empty.</summary>
	/// <remarks>Without a journal the record holds what this process did and nothing more.</remarks>
	public ActivityRecord(IActivityJournal? journal = null)
	{
		_journal = journal;

		foreach (ActivityJournalRow row in journal?.Load() ?? [])
		{
			_entries.Enqueue(new ActivityEntry(row.Sequence, row.Report));
			_sequence = row.Sequence;
		}
	}

	/// <summary>Every report the record still holds, newest first.</summary>
	/// <remarks>Wanting the count as well means <see cref="Read"/>: two separately locked reads leave a gap.</remarks>
	public IReadOnlyList<ActivityEntry> Entries
	{
		get
		{
			lock (_gate)
				return [.. _entries.Reverse()];
		}
	}

	/// <summary>The sequence of the newest report recorded, or zero where nothing has been.</summary>
	public long Newest
	{
		get
		{
			lock (_gate)
				return _sequence;
		}
	}

	public int Count
	{
		get
		{
			lock (_gate)
				return _entries.Count;
		}
	}

	public bool IsEmpty => Count == 0;

	/// <summary>The record and the count that goes with it, under one lock.</summary>
	public ActivityTimeline Read()
	{
		lock (_gate)
			return new ActivityTimeline([.. _entries.Reverse()], _sequence);
	}

	/// <summary>Records one report, evicting the oldest once the record is full.</summary>
	public ActivityEntry Record(HeatingReport report)
	{
		ArgumentNullException.ThrowIfNull(report);

		ActivityEntry entry;
		ActivityEntry[] bounded;

		lock (_gate)
		{
			entry = new ActivityEntry(++_sequence, report);
			_entries.Enqueue(entry);

			while (_entries.Count > Capacity)
				_entries.Dequeue();

			bounded = [.. _entries];
		}

		// Outside the lock: the journal only marks itself dirty here and the flusher writes it later, but neither
		// step should hold up the lock a pass over the rooms is about to want.
		_journal?.TrySave([.. bounded.Select(one => new ActivityJournalRow(one.Sequence, one.Report))]);

		return entry;
	}
}
