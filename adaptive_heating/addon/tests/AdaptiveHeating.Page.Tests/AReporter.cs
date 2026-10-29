using AdaptiveHeating.AddOn.Reporting;
using AdaptiveHeating.Planner;

using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveHeating.Page.Tests;

/// <summary>One card the add-on raised, as Home Assistant would have received it.</summary>
internal sealed record ACard(string Title, string Message);

/// <summary>Stands in for Home Assistant's own notifications, keeping what it was asked to raise.</summary>
internal sealed class ACardCollector : ITellsAPerson
{
	private readonly List<ACard> _raised = [];

	public IReadOnlyList<ACard> Raised => _raised;

	/// <summary>Whether the next call answers as though the card did not reach Home Assistant.</summary>
	public bool Refuses { get; set; }

	/// <inheritdoc/>
	public Task<bool> NotifyAsync(string kind, string? roomId, string title, string message, CancellationToken token)
	{
		_raised.Add(new ACard(title, message));

		return Task.FromResult(!Refuses);
	}
}

/// <summary>One line the add-on wrote into Home Assistant's logbook, with the room it was filed against.</summary>
internal sealed record ALogbookLine(string What, string Why, string? EntityId);

/// <summary>Stands in for Home Assistant's own logbook, keeping every line it was asked to write.</summary>
internal sealed class ALogbookCollector : ITellsTheLogbook
{
	private readonly List<ALogbookLine> _written = [];

	public IReadOnlyList<ALogbookLine> Written => _written;

	/// <summary>Whether the next call answers as though the line did not reach Home Assistant.</summary>
	public bool Refuses { get; set; }

	/// <inheritdoc/>
	public Task<bool> WriteAsync(string what, string why, string? entityId, CancellationToken token)
	{
		_written.Add(new ALogbookLine(what, why, entityId));

		return Task.FromResult(!Refuses);
	}
}

/// <summary>A reporter over a record and a collector, so a test can read both what was kept and what was raised.</summary>
internal sealed class AReporter
{
	/// <param name="household">The household's clock. UTC where a test is not about a zone, so a line is easy to read.</param>
	public AReporter(
		IActivityJournal? journal = null,
		Func<string, string>? nameOf = null,
		Func<string, string?>? thermostatOf = null,
		TheHouseholdClock? household = null,
		Func<string, string>? personWords = null,
		Func<string, string>? automationWords = null)
	{
		Record = new ActivityRecord(journal);
		Cards = new ACardCollector();
		Logbook = new ALogbookCollector();
		Reports = new ReportsWhatHappened(
			Record,
			Cards,
			Logbook,
			nameOf ?? (roomId => roomId),
			thermostatOf ?? (roomId => $"climate.{roomId}"),
			roomId => roomId,
			household ?? TheHouseholdClock.Of(TimeZoneInfo.Utc),
			personWords ?? (userId => userId),
			automationWords ?? (entityId => entityId),
			NullLogger<ReportsWhatHappened>.Instance);
	}

	public ActivityRecord Record { get; }

	public ACardCollector Cards { get; }

	/// <summary>Every line the reporter wrote into the logbook, which is one per report.</summary>
	public ALogbookCollector Logbook { get; }

	public ReportsWhatHappened Reports { get; }
}
