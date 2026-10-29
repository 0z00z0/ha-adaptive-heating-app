using System.Globalization;

namespace Adaptive.Settings;

/// <summary>A sun event a period boundary can be anchored to.</summary>
public enum SunEvent
{
	/// <summary>The boundary is a fixed clock time, not a sun event.</summary>
	None,

	Sunrise,

	Sunset
}

/// <summary>
///     A parsed <see cref="TimePeriodConfig.Start"/>. Resolving a sun event needs the day's sun times, which is why
///     parsing and resolution are split.
/// </summary>
/// <param name="FixedTime">The clock time, when <paramref name="SunEvent"/> is <see cref="SunEvent.None"/>.</param>
/// <param name="SunEvent">The anchoring sun event, if any.</param>
/// <param name="Offset">Offset from the sun event; may be negative.</param>
public sealed record PeriodStart(TimeOnly? FixedTime, SunEvent SunEvent, TimeSpan Offset)
{
	private const string SunriseToken = "sunrise";
	private const string SunsetToken = "sunset";

	/// <summary>
	///     Parses <c>"06:30"</c>, <c>"sunrise"</c>, <c>"sunset-01:00"</c> and friends. Culture-invariant, so a YAML
	///     file behaves identically on a Norwegian and an English host.
	/// </summary>
	public static bool TryParse(string? text, out PeriodStart? result)
	{
		result = null;
		if (string.IsNullOrWhiteSpace(text))
			return false;

		string trimmed = text.Trim();

		if (TimeOnly.TryParseExact(trimmed, ["HH:mm", "H:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly fixedTime))
		{
			result = new PeriodStart(fixedTime, SunEvent.None, TimeSpan.Zero);
			return true;
		}

		int signIndex = trimmed.IndexOfAny(['+', '-']);
		string token = (signIndex < 0 ? trimmed : trimmed[..signIndex]).Trim();

		SunEvent sunEvent = token.Equals(SunriseToken, StringComparison.OrdinalIgnoreCase) ? SunEvent.Sunrise
			: token.Equals(SunsetToken, StringComparison.OrdinalIgnoreCase) ? SunEvent.Sunset
			: SunEvent.None;

		if (sunEvent == SunEvent.None)
			return false;

		TimeSpan offset = TimeSpan.Zero;
		if (signIndex >= 0)
		{
			bool negative = trimmed[signIndex] == '-';
			string magnitude = trimmed[(signIndex + 1)..].Trim();
			if (!TimeSpan.TryParseExact(magnitude, ["hh\\:mm", "h\\:mm", "hh\\:mm\\:ss"], CultureInfo.InvariantCulture, out TimeSpan parsed))
				return false;

			offset = negative ? parsed.Negate() : parsed;
		}

		result = new PeriodStart(null, sunEvent, offset);
		return true;
	}

	/// <summary>
	///     Resolves this boundary to a time of day. A sun-anchored boundary whose sun time is unknown cannot be
	///     placed and returns <c>null</c>.
	/// </summary>
	public TimeOnly? Resolve(SunTimes sunTimes)
	{
		ArgumentNullException.ThrowIfNull(sunTimes);

		if (SunEvent == SunEvent.None)
			return FixedTime;

		TimeOnly? anchor = SunEvent == SunEvent.Sunrise ? sunTimes.Sunrise : sunTimes.Sunset;
		return anchor?.Add(Offset);
	}
}

/// <summary>The day's sun times, supplied by the caller so period resolution stays pure.</summary>
/// <param name="Sunrise">Local time of sunrise, or <c>null</c> when unknown (polar night, sun entity missing).</param>
/// <param name="Sunset">Local time of sunset, or <c>null</c> when unknown.</param>
public sealed record SunTimes(TimeOnly? Sunrise, TimeOnly? Sunset)
{
	/// <summary>Sun times that resolve nothing. Sun-anchored periods are skipped when this is all that is known.</summary>
	public static readonly SunTimes Unknown = new(null, null);
}
