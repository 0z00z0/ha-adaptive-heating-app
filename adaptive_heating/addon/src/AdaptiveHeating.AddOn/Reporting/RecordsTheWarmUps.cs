using System.Globalization;

using AdaptiveHeating.AddOn.Driving;
using AdaptiveHeating.AddOn.Integration;
using AdaptiveHeating.Page.Settings;

namespace AdaptiveHeating.AddOn.Reporting;

/// <summary>
/// Writes one entry per finished warm-up into the record calendar, and writes none of them twice.
/// </summary>
/// <remarks>
///     A warm-up cannot be seen finishing from outside: the integration clears the thermostat's own warm-up
///     attribute the moment the deadline passes and publishes its verdict nowhere. So what was asked for is
///     remembered here instead, in a note that survives a restart, because a restart between the asking and the
///     deadline would otherwise lose the entry.
///     <para>
///         A row is keyed on the room and the deadline, so asking twice for one warm-up replaces a row rather
///         than adding one, and the row is removed only once its entry is in the calendar. Nothing can take an
///         entry out of a calendar again, so a second one would stand for ever.
///     </para>
/// </remarks>
internal sealed class RecordsTheWarmUps
{
	/// <summary>How long a warm-up nobody could write down is kept. It bounds the note while the connection is away.</summary>
	public static readonly TimeSpan KeptWhileUnwritten = TimeSpan.FromDays(7);

	private readonly IWarmUpNote? _note;
	private readonly Calendars _calendars;
	private readonly ReportsWhatHappened _reports;
	private readonly Func<string, string> _nameOf;
	private readonly Lock _gate = new();
	private readonly Dictionary<string, WarmUpAsked> _asked = new(StringComparer.Ordinal);

	private bool _saidThereIsNowhere;

	/// <param name="nameOf">What a person calls a room, given its id. A row holds the id and never the name.</param>
	public RecordsTheWarmUps(
		IWarmUpNote? note,
		Calendars calendars,
		ReportsWhatHappened reports,
		Func<string, string> nameOf)
	{
		_note = note;
		_calendars = calendars ?? throw new ArgumentNullException(nameof(calendars));
		_reports = reports ?? throw new ArgumentNullException(nameof(reports));
		_nameOf = nameOf ?? throw new ArgumentNullException(nameof(nameOf));

		foreach (WarmUpAsked row in _note?.Load() ?? [])
			_asked[row.Key] = row;
	}

	/// <summary>What one warm-up is known by: the room it is in and the deadline it was planned against.</summary>
	public static string KeyOf(string roomId, DateTimeOffset deadline) =>
		$"{roomId}|{deadline.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}";

	/// <summary>Every warm-up still waiting to be written down.</summary>
	public IReadOnlyList<WarmUpAsked> Waiting
	{
		get
		{
			lock (_gate)
				return [.. _asked.Values];
		}
	}

	/// <summary>Notes that a warm-up has been asked for, so its finish is written down whatever happens between.</summary>
	public void Asked(string roomId, double wanted, DateTimeOffset deadline, DateTimeOffset startedAt)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(roomId);

		string key = KeyOf(roomId, deadline);

		lock (_gate)
		{
			// The same warm-up asked for again after a restart is the same row, so nothing is added and the
			// moment it was first asked at is kept.
			if (_asked.ContainsKey(key))
				return;

			_asked[key] = new WarmUpAsked(key, roomId, wanted, startedAt, deadline);
		}

		Save();
	}

	/// <summary>Writes down every warm-up whose deadline has passed. Answers how many entries reached the calendar.</summary>
	public async Task<int> WriteDownWhatFinishedAsync(
		HeatingDocument document,
		IReadOnlyList<EntityState> states,
		IReadOnlyDictionary<string, ThermostatNow> rooms,
		DateTimeOffset now,
		CancellationToken token)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(rooms);

		List<WarmUpAsked> finished = Finished(rooms, now);

		if (finished.Count == 0)
			return 0;

		string? calendar = document.RecordCalendar is { Length: > 0 } named
			&& CalendarsInTheHouse.IsThere(states, named)
				? named
				: null;

		int written = 0;

		foreach (WarmUpAsked warmUp in finished)
		{
			// Kept only while there is any chance of writing it down. Otherwise the note grows for as long as the
			// connection stays away.
			if (now - warmUp.Deadline > KeptWhileUnwritten)
			{
				Forget(warmUp.Key);

				continue;
			}

			if (calendar is null)
			{
				await NowhereToWriteItAsync(document.RecordCalendar, warmUp, now, token).ConfigureAwait(false);
				Forget(warmUp.Key);

				continue;
			}

			_saidThereIsNowhere = false;

			ActionOutcome outcome = await _calendars
				.WriteEntryAsync(calendar, Summary(warmUp), Description(warmUp), warmUp.Started, warmUp.Deadline, token)
				.ConfigureAwait(false);

			if (outcome == ActionOutcome.Called)
			{
				// Removed only once the entry is in the calendar, so nothing is lost while the connection is down.
				Forget(warmUp.Key);
				written++;

				await _reports
					.ReportAsync(
						HeatingReport.About(WhatHappened.WarmUpRecorded, now, warmUp.RoomId) with
						{
							Temperature = warmUp.Wanted,
							Against = warmUp.Reached,
							Deadline = warmUp.Deadline,
							Named = calendar
						},
						token)
					.ConfigureAwait(false);

				continue;
			}

			// A calendar with no such action, or one refusing the entry, is never going to take it. Anything else
			// is the connection, and the row waits for the next pass.
			if (outcome is ActionOutcome.UnknownAction or ActionOutcome.Refused)
			{
				await NowhereToWriteItAsync(calendar, warmUp, now, token).ConfigureAwait(false);
				Forget(warmUp.Key);
			}
		}

		return written;
	}

	/// <summary>The rows whose deadline has passed, each carrying what its room read when the deadline came round.</summary>
	private List<WarmUpAsked> Finished(IReadOnlyDictionary<string, ThermostatNow> rooms, DateTimeOffset now)
	{
		List<WarmUpAsked> finished = [];
		bool changed = false;

		lock (_gate)
		{
			foreach (string key in _asked.Keys.ToList())
			{
				WarmUpAsked row = _asked[key];

				if (row.Deadline > now)
					continue;

				// Taken on the first pass that sees the deadline gone, and then kept: a write retried hours later
				// would otherwise record a temperature the room reached long after it was due.
				if (row.Reached is null && rooms.TryGetValue(row.RoomId, out ThermostatNow? thermostat))
				{
					row = row with { Reached = thermostat.RoomTemperature };
					_asked[key] = row;
					changed = true;
				}

				finished.Add(row);
			}

			finished.Sort((left, right) => left.Deadline.CompareTo(right.Deadline));
		}

		if (changed)
			Save();

		return finished;
	}

	// Said once while there is nowhere to write one, and again the next time that becomes true. A cabin with no
	// record calendar would otherwise raise a card for every warm-up it ever runs.
	private async Task NowhereToWriteItAsync(string? calendar, WarmUpAsked warmUp, DateTimeOffset now, CancellationToken token)
	{
		if (_saidThereIsNowhere)
			return;

		_saidThereIsNowhere = await _reports
			.ReportAsync(
				HeatingReport.About(WhatHappened.WarmUpNotRecorded, now, warmUp.RoomId) with
				{
					Temperature = warmUp.Wanted,
					Deadline = warmUp.Deadline,
					Named = calendar
				},
				token)
			.ConfigureAwait(false);
	}

	private void Forget(string key)
	{
		lock (_gate)
		{
			if (!_asked.Remove(key))
				return;
		}

		Save();
	}

	private void Save()
	{
		WarmUpAsked[] rows;

		lock (_gate)
			rows = [.. _asked.Values];

		_note?.TrySave(rows);
	}

	private string Summary(WarmUpAsked warmUp) =>
		warmUp.Reached is null
			? $"{_nameOf(warmUp.RoomId)} warm-up, no reading"
			: $"{_nameOf(warmUp.RoomId)} warm-up {(warmUp.Met ? "met" : "missed")}";

	// Short on purpose: a calendar entry is read in a month view, where a sentence is a row of dots.
	private static string Description(WarmUpAsked warmUp) =>
		$"Wanted {Degrees(warmUp.Wanted)}, reached {Degrees(warmUp.Reached)}";

	private static string Degrees(double? value) =>
		value is { } number ? number.ToString("0.#", CultureInfo.InvariantCulture) + " °C" : "—";
}
