using Adaptive.Settings;

namespace Adaptive.UI;

/// <summary>Text helpers for <see cref="TimePeriodConfig.Start"/>: composing start strings, and describing what the engine makes of one.</summary>
/// <remarks>
///     The grammar belongs to <see cref="PeriodStart.TryParse"/> and only there. Everything here either produces
///     a string for that parser or feeds one through it; nothing re-implements the parse.
/// </remarks>
public static class PeriodStartText
{
	/// <summary>Composes a fixed clock-time boundary: <c>"22:30"</c>.</summary>
	public static string Clock(TimeOnly time) =>
		time.ToString("HH\\:mm", CultureInfo.InvariantCulture);

	/// <summary>Composes a sun-anchored boundary: <c>"sunrise"</c>, <c>"sunset-01:00"</c>, <c>"sunrise+00:45"</c>.</summary>
	public static string Sun(SunEvent anchor, int offsetMinutes)
	{
		var token = anchor switch
		{
			SunEvent.Sunrise => "sunrise",
			SunEvent.Sunset => "sunset",
			_ => throw new ArgumentOutOfRangeException(nameof(anchor), anchor, "A sun-anchored boundary needs a sun event.")
		};

		if (offsetMinutes == 0)
			return token;

		var magnitude = Math.Abs(offsetMinutes);
		return string.Create(CultureInfo.InvariantCulture,
			$"{token}{(offsetMinutes < 0 ? '-' : '+')}{magnitude / 60:00}:{magnitude % 60:00}");
	}

	/// <summary>The rooms whose movement may start a period, or <c>null</c> when none is named and any watched room will do.</summary>
	public static string? MotionRooms(IReadOnlyList<string>? roomNames)
	{
		if (roomNames is not { Count: > 0 })
			return null;

		List<string> named = [.. roomNames.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim())];

		return named.Count switch
		{
			0 => null,
			1 => named[0],
			2 => $"{named[0]} or {named[1]}",
			_ => $"{named[0]} or {named.Count - 1} other rooms"
		};
	}

	/// <summary>The fold header's start line for a period that does not begin on the clock: the boundary, then what it waits for.</summary>
	public static string WaitsForMovement(string start, IReadOnlyList<string>? roomNames) =>
		MotionRooms(roomNames) is { } rooms
			? $"{start} · waits for movement in {rooms}"
			: $"{start} · waits for movement";

	/// <summary>The same boundary as <see cref="Describe"/> with the prose stripped, for a row that already
	/// names the period: <c>"06:45"</c>, <c>"sunrise"</c>, <c>"sunset -01:00"</c>.</summary>
	public static string? Short(string? start)
	{
		if (!PeriodStart.TryParse(start, out PeriodStart? parsed) || parsed is null)
			return null;

		if (parsed.FixedTime is { } time)
			return time.ToString("HH\\:mm", CultureInfo.InvariantCulture);

		string anchor = parsed.SunEvent == SunEvent.Sunrise ? "sunrise" : "sunset";

		if (parsed.Offset == TimeSpan.Zero)
			return anchor;

		string sign = parsed.Offset < TimeSpan.Zero ? "-" : "+";

		return string.Create(CultureInfo.InvariantCulture, $"{anchor} {sign}{parsed.Offset.Duration():hh\\:mm}");
	}

	/// <summary>What the engine will make of a Start string, in words, or <c>null</c> when it will refuse it.</summary>
	public static string? Describe(string? start)
	{
		if (!PeriodStart.TryParse(start, out PeriodStart? parsed) || parsed is null)
			return null;

		if (parsed.FixedTime is { } time)
			return $"every day at {time:HH\\:mm}";

		var anchor = parsed.SunEvent == SunEvent.Sunrise ? "sunrise" : "sunset";

		if (parsed.Offset == TimeSpan.Zero)
			return $"at {anchor}";

		TimeSpan magnitude = parsed.Offset.Duration();
		var direction = parsed.Offset < TimeSpan.Zero ? "before" : "after";

		return $"{magnitude:hh\\:mm} {direction} {anchor}";
	}
}
