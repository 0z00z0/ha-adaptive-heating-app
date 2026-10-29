using Adaptive.Settings;

namespace AdaptiveLighting.Configuration;

/// <summary>The physical bounds and the period lookups the validation sections share.</summary>
internal static class ValidationRanges
{
	// The thermostat's own bounds: the integration's core/defaults.py and the page's NumberField hold the same pair.
	internal const double MinTemperature = 2;
	internal const double MaxTemperature = 30;

	// The frost and the lowest night temperature. Below the thermostat's own bounds: the device's minimum still
	// holds wherever either is written.
	internal const double MinFrostTemperature = -20;

	/// <summary>The period a reference names, or <c>null</c> when nothing does.</summary>
	internal static TimePeriodConfig? PeriodWithKey(IReadOnlyList<TimePeriodConfig> periods, string? key) =>
		key is { Length: > 0 } ? periods.ByKey(key) : null;

	/// <summary>A period reference as a sentence should name it: its display name, or the raw id when it resolves to nothing.</summary>
	internal static string PeriodLabel(IReadOnlyList<TimePeriodConfig> periods, string? key) =>
		PeriodWithKey(periods, key)?.Name is { Length: > 0 } name ? name : key?.Trim() ?? "";
}
