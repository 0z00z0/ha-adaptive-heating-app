namespace AdaptiveHeating.AddOn.Integration;

/// <summary>Home Assistant's own calendar actions and the names in their answers.</summary>
/// <remarks>
///     Read from <c>calendar/services.yaml</c> and <c>calendar/__init__.py</c> on 2026-09-25. Only two actions
///     exist: one creates an entry and one reads entries over a stretch of time. Nothing changes or removes one,
///     which is what makes a calendar a record rather than a working document.
/// </remarks>
internal static class CalendarActions
{
	public const string Domain = "calendar";

	/// <summary>Reads entries between two moments. It answers only through the response channel.</summary>
	public const string GetEvents = "get_events";

	public const string CreateEvent = "create_event";

	public const string StartDateTime = "start_date_time";
	public const string EndDateTime = "end_date_time";
	public const string Summary = "summary";
	public const string Description = "description";

	/// <summary>The answer is keyed by the entity it came from, and each holds a list under this name.</summary>
	public const string EventsField = "events";

	public const string StartField = "start";

	/// <summary>The integration behind a local calendar, which is the only kind the record may be written to.</summary>
	public const string LocalPlatform = "local_calendar";
}
