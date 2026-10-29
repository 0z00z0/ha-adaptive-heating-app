using AdaptiveHeating.Page.Settings;
using AdaptiveHeating.Planner;

using ActivityCategory = AdaptiveLighting.Web.Presentation.ActivityCategory;

namespace AdaptiveHeating.AddOn.Reporting;

/// <summary>
/// Where everything the add-on says goes: the activity record, the log, and a card in Home Assistant for the few
/// things a person has to see.
/// </summary>
/// <remarks>
///     One call, three destinations, so nothing the add-on does is said in only one of them. The record is bounded
///     and journalled, so a restart keeps it; the log is the durable copy; the card is only for what the add-on
///     cannot put right itself.
/// </remarks>
internal sealed class ReportsWhatHappened : IRoomActivity, IActivityRecord
{
	private readonly ActivityRecord _record;
	private readonly ITellsAPerson _person;
	private readonly ITellsTheLogbook _logbook;
	private readonly Func<string, string> _nameOf;
	private readonly Func<string, string?> _thermostatOf;
	private readonly Func<string, string?> _areaOf;
	private readonly Func<string, string> _personWords;
	private readonly Func<string, string> _automationWords;
	private readonly TheHouseholdClock _household;
	private readonly ILogger<ReportsWhatHappened> _logger;

	/// <param name="record">The bounded, journalled record every report is kept in.</param>
	/// <param name="person">Where a card goes for the few reports that need one.</param>
	/// <param name="logbook">Home Assistant's own record, which every report is written to as well.</param>
	/// <param name="nameOf">What a person calls a room, given its id. The record holds the id and never the name.</param>
	/// <param name="thermostatOf">The room's own entity, so the Activity panel beside it filters to its lines.</param>
	/// <param name="areaOf">The area a room is in now, which the pages link to and filter by.</param>
	/// <param name="household">The household's clock, which every time in a line is rendered on.</param>
	/// <param name="personWords">The words for a person, given a Home Assistant user id. The record holds the id.</param>
	/// <param name="automationWords">The words for an automation, given its entity id. The record holds the id.</param>
	/// <param name="logger">The log both the line and the card are also written to.</param>
	public ReportsWhatHappened(
		ActivityRecord record,
		ITellsAPerson person,
		ITellsTheLogbook logbook,
		Func<string, string> nameOf,
		Func<string, string?> thermostatOf,
		Func<string, string?> areaOf,
		TheHouseholdClock household,
		Func<string, string> personWords,
		Func<string, string> automationWords,
		ILogger<ReportsWhatHappened> logger)
	{
		_record = record ?? throw new ArgumentNullException(nameof(record));
		_person = person ?? throw new ArgumentNullException(nameof(person));
		_logbook = logbook ?? throw new ArgumentNullException(nameof(logbook));
		_nameOf = nameOf ?? throw new ArgumentNullException(nameof(nameOf));
		_thermostatOf = thermostatOf ?? throw new ArgumentNullException(nameof(thermostatOf));
		_areaOf = areaOf ?? throw new ArgumentNullException(nameof(areaOf));
		_household = household ?? throw new ArgumentNullException(nameof(household));
		_personWords = personWords ?? throw new ArgumentNullException(nameof(personWords));
		_automationWords = automationWords ?? throw new ArgumentNullException(nameof(automationWords));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <summary>What the add-on has recently done, for a reader of the record.</summary>
	public ActivityTimeline Recent() => _record.Read();

	/// <inheritdoc/>
	public IReadOnlyList<RoomLogRow> For(string roomId) =>
	[
		.. _record.Entries
			.Where(entry => string.Equals(entry.RoomId, roomId, StringComparison.Ordinal))
			.Select(entry =>
			{
				ActivityLine line = ReportWords.Describe(Shown(entry.Report), NameOf(entry.RoomId), _household);

				return new RoomLogRow(entry.Sequence, entry.At, line.What, line.Why);
			})
	];

	/// <inheritdoc/>
	public int Capacity => ActivityRecord.Capacity;

	/// <inheritdoc/>
	public RecordRead Read()
	{
		ActivityTimeline timeline = _record.Read();

		return new RecordRead([.. timeline.Entries.Select(Recorded)], timeline.Newest);
	}

	private RecordedReport Recorded(ActivityEntry entry)
	{
		ActivityLine line = ReportWords.Describe(Shown(entry.Report), NameOf(entry.RoomId), _household);
		string? room = entry.RoomId is { Length: > 0 } ? NameOf(entry.RoomId) : null;

		// The row is kept by room id, so a moved room keeps its lines; the pages reach the room through its area now.
		string? area = entry.RoomId is { Length: > 0 } roomId ? _areaOf(roomId) ?? roomId : null;

		return new RecordedReport(entry.Sequence, entry.At, area, room, line.What, line.Why, CategoryOf(entry.Report.What));
	}

	/// <summary>The report's kind in snake case, which a card's id is made from.</summary>
	internal static string KindOf(WhatHappened what) =>
		string.Concat(what.ToString().Select((character, at) =>
			char.IsUpper(character) ? (at > 0 ? "_" : "") + char.ToLowerInvariant(character) : character.ToString()));

	/// <summary>Which of the activity page's chips a report falls under.</summary>
	internal static ActivityCategory CategoryOf(WhatHappened what) => what switch
	{
		WhatHappened.ModeInForce => ActivityCategory.Mode,
		WhatHappened.RoomHolds or WhatHappened.RoomSwitched or WhatHappened.WarmUpStarted => ActivityCategory.TemperatureChange,
		WhatHappened.HandChange or WhatHappened.HandChangeEnded => ActivityCategory.ManualChange,
		WhatHappened.CannotWarmInTime
			or WhatHappened.OutdoorSensorsAllFailed
			or WhatHappened.ArrivalCalendarMissing
			or WhatHappened.WarmUpNotRecorded
			or WhatHappened.HouseholdZoneNotResolved => ActivityCategory.Declined,
		_ => ActivityCategory.Background
	};

	/// <summary>Files one report, and raises a card for it where it needs a person.</summary>
	/// <returns>
	///     Whether nothing about this report is outstanding: false only where a card was needed and did not reach
	///     Home Assistant. A latch that stops the same card arriving every minute reads this, so a card refused
	///     while the connection was down is raised again by the next pass.
	/// </returns>
	public async Task<bool> ReportAsync(HeatingReport report, CancellationToken token)
	{
		ArgumentNullException.ThrowIfNull(report);

		_record.Record(report);

		ActivityLine line = ReportWords.Describe(Shown(report), NameOf(report.RoomId), _household);

		await _logbook
			.WriteAsync(line.What, line.Why, ThermostatOf(report.RoomId), token)
			.ConfigureAwait(false);

		if (report.NeedsAPerson)
		{
			_logger.LogWarning("{What}. {Why}", line.What, line.Why);

			return await _person.NotifyAsync(KindOf(report.What), report.RoomId, line.What, line.Why, token).ConfigureAwait(false);
		}

		_logger.LogInformation("{What}. {Why}", line.What, line.Why);

		return true;
	}

	private HeatingReport Shown(HeatingReport report) => report switch
	{
		{ ChangedByUserId: { Length: > 0 } userId } => report with { Named = _personWords(userId) },
		{ ChangedByAutomationId: { Length: > 0 } automation } => report with { Named = _automationWords(automation) },
		_ => report
	};

	// A room the settings no longer hold reads as its own id, which is still something a person can look up.
	private string NameOf(string? roomId) => roomId is { Length: > 0 } named ? _nameOf(named) : "The cabin";

	// A house-wide report carries no entity, so the logbook holds its line with no room to filter by.
	private string? ThermostatOf(string? roomId) => roomId is { Length: > 0 } named ? _thermostatOf(named) : null;
}
