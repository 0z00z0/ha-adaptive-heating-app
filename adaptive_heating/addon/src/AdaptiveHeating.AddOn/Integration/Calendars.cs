using System.Globalization;
using System.Text.Json;

using AdaptiveHeating.Planner;

namespace AdaptiveHeating.AddOn.Integration;

/// <summary>The two things Home Assistant lets anything do with a calendar: read a stretch of it, and add an entry.</summary>
/// <remarks>
///     The arrivals are read over a stretch of time and never off the entity's state, because the state names only
///     the nearest event and a cabin holds more than one arrival ahead of it.
/// </remarks>
internal sealed class Calendars
{
	private readonly CoreApi _api;

	public Calendars(CoreApi api) =>
		_api = api ?? throw new ArgumentNullException(nameof(api));

	/// <summary>Reads one calendar between two moments, or <c>null</c> where the read did not go through.</summary>
	public async Task<ArrivalWindow?> ReadAsync(
		string calendar,
		DateTimeOffset from,
		DateTimeOffset until,
		CancellationToken token)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(calendar);

		(ActionOutcome outcome, JsonElement? answer) = await _api
			.CallAsync(
				CalendarActions.Domain,
				CalendarActions.GetEvents,
				calendar,
				new Dictionary<string, object?>(StringComparer.Ordinal)
				{
					[CalendarActions.StartDateTime] = CoreApi.AsTime(from),
					[CalendarActions.EndDateTime] = CoreApi.AsTime(until)
				},
				wantsAnAnswer: true,
				token)
			.ConfigureAwait(false);

		if (outcome != ActionOutcome.Called || answer is not { } result)
			return null;

		return ArrivalWindow.Of(from, until, ArrivalsIn(result, calendar));
	}

	/// <summary>Adds one entry to a calendar. An entry cannot be changed or taken away afterwards.</summary>
	public Task<ActionOutcome> WriteEntryAsync(
		string calendar,
		string summary,
		string description,
		DateTimeOffset from,
		DateTimeOffset until,
		CancellationToken token)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(calendar);

		// A calendar refuses an entry that ends before it starts, and a warm-up asked for at its own deadline is
		// exactly that long.
		DateTimeOffset ends = until > from ? until : from.AddMinutes(1);

		return _api.CallAsync(
			CalendarActions.Domain,
			CalendarActions.CreateEvent,
			calendar,
			new Dictionary<string, object?>(StringComparer.Ordinal)
			{
				[CalendarActions.Summary] = summary,
				[CalendarActions.Description] = description,
				[CalendarActions.StartDateTime] = CoreApi.AsTime(from),
				[CalendarActions.EndDateTime] = CoreApi.AsTime(ends)
			},
			token);
	}

	/// <summary>Every arrival in the answer. The response is keyed by the entity it came from.</summary>
	private static IEnumerable<DateTimeOffset> ArrivalsIn(JsonElement result, string calendar)
	{
		if (result.ValueKind != JsonValueKind.Object
			|| !result.TryGetProperty("response", out JsonElement response)
			|| response.ValueKind != JsonValueKind.Object
			|| !response.TryGetProperty(calendar, out JsonElement mine)
			|| mine.ValueKind != JsonValueKind.Object
			|| !mine.TryGetProperty(CalendarActions.EventsField, out JsonElement events)
			|| events.ValueKind != JsonValueKind.Array)
			yield break;

		foreach (JsonElement entry in events.EnumerateArray())
		{
			if (entry.ValueKind != JsonValueKind.Object
				|| !entry.TryGetProperty(CalendarActions.StartField, out JsonElement start)
				|| start.ValueKind != JsonValueKind.String)
				continue;

			if (Arrival(start.GetString()) is { } at)
				yield return at;
		}
	}

	// An entry written as a whole day carries a date and no time. Reading one as midnight would start the heating
	// the night before, so it is no arrival at all.
	private static DateTimeOffset? Arrival(string? start) =>
		start is { Length: > 0 } written
		&& written.Contains('T', StringComparison.Ordinal)
		&& DateTimeOffset.TryParse(written, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset at)
			? at
			: null;
}
