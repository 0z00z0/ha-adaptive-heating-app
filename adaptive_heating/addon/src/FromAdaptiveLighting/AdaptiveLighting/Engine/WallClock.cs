namespace Adaptive.Engine;

// IScheduler.Now is a DateTimeOffset at +00:00, so its TimeOfDay is UTC. Every Start in the document is a
// household wall clock, so the conversion has to happen before the two are compared.
internal static class WallClock
{
	internal static TimeOnly TimeIn(this DateTimeOffset instant, TimeZoneInfo zone) =>
		TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

	internal static DateOnly DayIn(this DateTimeOffset instant, TimeZoneInfo zone) =>
		DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
}
