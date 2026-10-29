using AdaptiveLighting.Configuration;

namespace Adaptive.Settings;

/// <summary>The circadian table: ids, starts, targets, and the rooms whose movement may begin a period.</summary>
internal static class PeriodRules
{
	internal static void Validate(List<TimePeriodConfig> periods, ValidationResult result)
	{
		// Names are free to repeat: nothing resolves by one any more. Two rows answering to one key are not, or the
		// first would silently take every reference to the second.
		IEnumerable<string> duplicateKeys = periods
			.Where(p => p.Key is { Length: > 0 })
			.GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
			.Where(g => g.Count() > 1)
			.Select(g => g.Key);

		foreach (string key in duplicateKeys)
			result.AddError($"Two periods share the id '{key}'. Give one of them an Id of its own, or delete it and add it again.");

		Dictionary<TimeOnly, string> fixedStarts = new();

		foreach (TimePeriodConfig period in periods)
		{
			if (!PeriodStart.TryParse(period.Start, out PeriodStart? start))
				continue;   // start problems are reported in the per-period pass below

			// Sun-anchored boundaries move daily, so an overlap between them cannot be decided here.
			if (start!.FixedTime is { } time)
			{
				if (fixedStarts.TryGetValue(time, out string? other))
					result.AddError($"Periods '{other}' and '{period.Name}' both start at {time:HH\\:mm}.");
				else
					fixedStarts[time] = period.Name;
			}
		}

		foreach (TimePeriodConfig period in periods)
		{
			if (string.IsNullOrWhiteSpace(period.Name))
				result.AddError("A period has no Name.");

			if (!PeriodStart.TryParse(period.Start, out _))
			{
				result.AddError($"Period '{period.Name}' has an unparseable Start '{period.Start}'. Expected \"HH:mm\", \"sunrise\", \"sunset\", or a sun event with an offset such as \"sunset-01:00\".");
				continue;
			}

			ValidateTargets(period, result);
		}
	}

	private static void ValidateTargets(TimePeriodConfig period, ValidationResult result)
	{
		if (period.Temperature is < ValidationRanges.MinTemperature or > ValidationRanges.MaxTemperature)
			result.AddError($"Period '{period.Name}' has Temperature {period.Temperature}, outside {ValidationRanges.MinTemperature}–{ValidationRanges.MaxTemperature}.");
	}
}
