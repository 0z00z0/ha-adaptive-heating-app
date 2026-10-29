using System.Globalization;
using System.Text.Json;

using AdaptiveHeating.AddOn.Integration;
using AdaptiveHeating.AddOn.Reporting;

namespace AdaptiveHeating.Page.Tests;

/// <summary>A Home Assistant calendar holding entries, answering a read of a stretch of it the way the real one does.</summary>
/// <remarks>
///     The stretch is honoured rather than ignored: an entry outside what the add-on asked for is left out here,
///     so a test asserting that a distant arrival is not read measures the add-on's own window and not the script.
/// </remarks>
internal sealed class ACalendar
{
	public const string Plan = "calendar.adaptiveheating_plan";
	public const string Record = "calendar.adaptiveheating_log";

	private readonly List<DateTimeOffset> _entries;
	private readonly string _entityId;

	private ACalendar(string entityId, IEnumerable<DateTimeOffset> entries)
	{
		_entityId = entityId;
		_entries = [.. entries];
	}

	/// <summary>Every read of the calendar, as the stretch it asked for.</summary>
	public List<(DateTimeOffset From, DateTimeOffset Until)> Reads { get; } = [];

	/// <summary>Every entry added to it, in the order they arrived.</summary>
	public List<(string Summary, string Description, DateTimeOffset From, DateTimeOffset Until)> Written { get; } = [];

	public static ACalendar Holding(params DateTimeOffset[] entries) => new(Plan, entries);

	/// <summary>One calendar entity as Home Assistant reports it, for the house a pass reads.</summary>
	public static string Entity(string entityId, string name) =>
		$$"""
		{ "entity_id": "{{entityId}}", "state": "off", "attributes": { "friendly_name": "{{name}}" } }
		""";

	/// <summary>Answers a read of this calendar and takes an entry written to the record. Anything else worked.</summary>
	public string? Answer(SentCall call)
	{
		ArgumentNullException.ThrowIfNull(call);

		if (string.Equals(call.Action, $"{CalendarActions.Domain}.{CalendarActions.GetEvents}", StringComparison.Ordinal))
			return Read(call);

		if (string.Equals(call.Action, $"{CalendarActions.Domain}.{CalendarActions.CreateEvent}", StringComparison.Ordinal))
			return Write(call);

		return CoreUnderTest.Worked(call.Id);
	}

	/// <summary>A date with no time at all, which is how a whole-day entry comes back.</summary>
	public static string WholeDay(DateTimeOffset day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

	/// <summary>Answers a read with entries this test wrote itself, whatever shape they are in.</summary>
	public static string ReadAnswering(int id, string calendar, params string[] starts)
	{
		string events = string.Join(
			",",
			starts.Select(start => $$"""{ "start": "{{start}}", "end": "{{start}}", "summary": "Arrival" }"""));

		return $$"""
			{
				"id": {{Invariant(id)}},
				"type": "result",
				"success": true,
				"result": { "response": { "{{calendar}}": { "events": [{{events}}] } }, "context": {} }
			}
			""";
	}

	private string Read(SentCall call)
	{
		(DateTimeOffset from, DateTimeOffset until) = Stretch(call);

		Reads.Add((from, until));

		string[] inside = [.. _entries
			.Where(entry => entry >= from && entry <= until)
			.Select(CoreApi.AsTime)];

		return ReadAnswering(call.Id, _entityId, inside);
	}

	private string Write(SentCall call)
	{
		using JsonDocument parsed = JsonDocument.Parse(call.Json);
		JsonElement fields = parsed.RootElement.GetProperty("service_data");

		(DateTimeOffset from, DateTimeOffset until) = Stretch(call);

		Written.Add((
			fields.GetProperty(CalendarActions.Summary).GetString() ?? "",
			fields.GetProperty(CalendarActions.Description).GetString() ?? "",
			from,
			until));

		return CoreUnderTest.Worked(call.Id);
	}

	private static (DateTimeOffset From, DateTimeOffset Until) Stretch(SentCall call)
	{
		using JsonDocument parsed = JsonDocument.Parse(call.Json);
		JsonElement fields = parsed.RootElement.GetProperty("service_data");

		return (
			Moment(fields.GetProperty(CalendarActions.StartDateTime).GetString()),
			Moment(fields.GetProperty(CalendarActions.EndDateTime).GetString()));
	}

	private static DateTimeOffset Moment(string? written) =>
		DateTimeOffset.Parse(written!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

	private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The warm-ups awaiting the record, kept in memory, so two runs can share one note as a restart does.</summary>
internal sealed class ANote : IWarmUpNote
{
	private IReadOnlyList<WarmUpAsked> _rows = [];

	/// <summary>How many times the note was written, which is what says a save happened at all.</summary>
	public int Saves { get; private set; }

	/// <inheritdoc/>
	public IReadOnlyList<WarmUpAsked> Load() => _rows;

	/// <inheritdoc/>
	public bool TrySave(IReadOnlyList<WarmUpAsked> rows)
	{
		_rows = [.. rows];
		Saves++;

		return true;
	}
}
