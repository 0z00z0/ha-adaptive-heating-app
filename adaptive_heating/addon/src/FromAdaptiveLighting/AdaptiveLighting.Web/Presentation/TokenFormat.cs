using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Web.Presentation;

/// <summary>How a value is written in a sentence, and how it is carried back.</summary>
/// <remarks>
///     Invariant culture throughout, in the written form as well as the carried one: a value written on an
///     <c>nb-NO</c> host has to parse on an <c>en-US</c> one, and a decimal comma in an attribute reads as no
///     number at all.
/// </remarks>
public static class TokenFormat
{
	/// <summary>A span of seconds, in the largest unit that stays whole: 90 seconds is "1 min 30 s".</summary>
	public static string Duration(int totalSeconds)
	{
		if (totalSeconds <= 0)
			return "0 s";

		if (totalSeconds < 60)
			return $"{totalSeconds} s";

		int minutes = totalSeconds / 60;
		int seconds = totalSeconds % 60;

		if (minutes < 60)
			return seconds == 0 ? $"{minutes} min" : $"{minutes} min {seconds} s";

		int hours = minutes / 60;
		int restMinutes = minutes % 60;

		if (seconds != 0)
			return $"{hours} h {restMinutes} min {seconds} s";

		// The ladder stops at hours. A lighting timeout measured in days is a mistake worth showing.
		return restMinutes == 0 ? $"{hours} h" : $"{hours} h {restMinutes} min";
	}

	/// <summary>A span given in minutes, which is how several settings are stored.</summary>
	public static string DurationFromMinutes(int totalMinutes) => Duration(totalMinutes * 60);

	/// <summary>A proportion 0-100: a number, a space, a percent sign, with real fractions surviving as 12.5 %.</summary>
	public static string Percent(double percent) =>
		$"{percent.ToString("0.##", CultureInfo.InvariantCulture)} %";

	/// <summary>A proportion held as the schema's 0-1 factor.</summary>
	public static string PercentFromFraction(double fraction) => Percent(fraction * 100);

	/// <summary>A quantity with its unit: "40 lx", and "3°" with no space, as typographic convention has it.</summary>
	public static string Number(double value, string unit = "")
	{
		string written = value.ToString("0.##", CultureInfo.InvariantCulture);

		if (unit.Length == 0)
			return written;

		return unit == "°" ? written + unit : $"{written} {unit}";
	}

	/// <summary>The canonical carrying form: invariant, no thousands separator, no trailing zeros.</summary>
	public static string Carry(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}

/// <summary>The curated shortlists a token's popover offers.</summary>
/// <remarks>These builders exist so a page cannot offer a value written one way and carried another.</remarks>
public static class TokenChoices
{
	/// <summary>Durations, written in the largest exact unit and carried in seconds.</summary>
	public static IReadOnlyList<TokenChoice> Durations(params int[] seconds)
	{
		ArgumentNullException.ThrowIfNull(seconds);

		return [.. seconds.Select(value => new TokenChoice(TokenFormat.Duration(value), TokenFormat.Carry(value)))];
	}

	/// <summary>Durations offered in minutes but still carried in seconds, for the settings the schema keeps in minutes.</summary>
	public static IReadOnlyList<TokenChoice> DurationsInMinutes(params int[] minutes)
	{
		ArgumentNullException.ThrowIfNull(minutes);

		return
		[
			.. minutes.Select(value => new TokenChoice(TokenFormat.DurationFromMinutes(value), TokenFormat.Carry(value * 60)))
		];
	}

	/// <summary>Percentages, written with the sign and carried as 0-100.</summary>
	public static IReadOnlyList<TokenChoice> Percentages(params double[] percents)
	{
		ArgumentNullException.ThrowIfNull(percents);

		return [.. percents.Select(value => new TokenChoice(TokenFormat.Percent(value), TokenFormat.Carry(value)))];
	}

	/// <summary>Quantities sharing one unit.</summary>
	public static IReadOnlyList<TokenChoice> Numbers(string unit, params double[] values)
	{
		ArgumentNullException.ThrowIfNull(values);

		return [.. values.Select(value => new TokenChoice(TokenFormat.Number(value, unit), TokenFormat.Carry(value)))];
	}

	/// <summary>Named options, written as given and carried as given.</summary>
	public static IReadOnlyList<TokenChoice> Of(params (string Text, string Value)[] options)
	{
		ArgumentNullException.ThrowIfNull(options);

		return [.. options.Select(option => new TokenChoice(option.Text, option.Value))];
	}
}
