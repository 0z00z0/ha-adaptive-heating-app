using AdaptiveHeating.AddOn.Integration;
using AdaptiveHeating.AddOn.Reporting;
using AdaptiveHeating.Page.Settings;
using AdaptiveHeating.Planner;

namespace AdaptiveHeating.AddOn.Driving;

/// <summary>
/// The planned arrivals, read off the calendar a person chose and held between readings.
/// </summary>
/// <remarks>
///     Read over a stretch of time and never off the entity's state: the state names only the nearest event, so a
///     cabin with two visits ahead of it would show one and hide the other. The reading is taken again on a
///     cadence rather than on every pass, because a pass runs once a minute and a calendar changes when somebody
///     edits it.
/// </remarks>
internal sealed class TheArrivals
{
	private readonly Calendars _calendars;
	private readonly ReportsWhatHappened _reports;
	private readonly ILogger<TheArrivals> _logger;

	private ArrivalWindow _window = ArrivalWindow.Unread;
	private string? _readFrom;
	private bool _reportedItIsMissing;
	private bool _saidThereIsNone;

	public TheArrivals(Calendars calendars, ReportsWhatHappened reports, ILogger<TheArrivals> logger)
	{
		_calendars = calendars ?? throw new ArgumentNullException(nameof(calendars));
		_reports = reports ?? throw new ArgumentNullException(nameof(reports));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <summary>The arrivals as the last reading left them, which is what a test reads to see them all.</summary>
	public ArrivalWindow Window => _window;

	/// <summary>A fresh connection to Home Assistant: the held reading is stale and anything said stands again.</summary>
	public void TheConnectionCameBack()
	{
		_reportedItIsMissing = false;
		_window = ArrivalWindow.Unread;
	}

	/// <summary>The deadline a warm-up plans backwards from, or <c>null</c> where there is none to plan against.</summary>
	public async Task<DateTimeOffset?> NextAsync(
		HeatingDocument document,
		IReadOnlyList<EntityState> states,
		DateTimeOffset now,
		CancellationToken token)
	{
		ArgumentNullException.ThrowIfNull(document);

		if (document.ArrivalCalendar is not { Length: > 0 } calendar)
		{
			Forget();

			if (!_saidThereIsNone)
			{
				_saidThereIsNone = true;
				_logger.LogDebug("No arrival calendar is chosen, so no warm-up is planned against a deadline.");
			}

			return null;
		}

		_saidThereIsNone = false;

		if (!CalendarsInTheHouse.IsThere(states, calendar))
		{
			Forget();
			await ReportItIsMissingAsync(calendar, now, token).ConfigureAwait(false);

			return null;
		}

		_reportedItIsMissing = false;

		// A different calendar is a different question, so nothing the old one said is carried over.
		if (!string.Equals(_readFrom, calendar, StringComparison.Ordinal))
			_window = ArrivalWindow.Unread;

		if (_window.IsStaleAt(now))
		{
			// A read that did not go through leaves the last one standing: an arrival already known is a better
			// deadline than none while the connection is down.
			if (await _calendars.ReadAsync(calendar, now, now + ArrivalWindow.Length, token).ConfigureAwait(false) is { } read)
			{
				_window = read;
				_readFrom = calendar;
			}
		}

		return _window.Next(now);
	}

	private void Forget()
	{
		_window = ArrivalWindow.Unread;
		_readFrom = null;
	}

	// Latched on the card arriving, never on the attempt, so one refused for want of a connection is raised again.
	private async Task ReportItIsMissingAsync(string calendar, DateTimeOffset now, CancellationToken token)
	{
		if (_reportedItIsMissing)
			return;

		_reportedItIsMissing = await _reports
			.ReportAsync(HeatingReport.About(WhatHappened.ArrivalCalendarMissing, now) with { Named = calendar }, token)
			.ConfigureAwait(false);
	}
}
