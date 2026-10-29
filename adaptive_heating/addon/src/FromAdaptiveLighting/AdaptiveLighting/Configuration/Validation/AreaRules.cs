using AdaptiveLighting.Configuration;

namespace Adaptive.Settings;

/// <summary>The rooms: the Area each one names, and the entities it names by hand.</summary>
internal static class AreaRules
{
	internal static void Validate(HeatingConfig config, ValidationContext known, ValidationResult result)
	{
		// An empty room list is what a fresh install starts from, so it warns instead of stopping the add-on.
		if (config.Areas.Count == 0)
		{
			result.AddWarning("No rooms yet. Label one heater per room in Home Assistant, then set the rooms up.");

			return;
		}

		// On the area id, never the name: the id is the key every setting hangs on, and a name stays free to
		// change and free to repeat.
		IEnumerable<string> duplicates = config.Areas
			.Where(area => area.AreaId is { Length: > 0 })
			.GroupBy(area => area.AreaId!, StringComparer.Ordinal)
			.Where(same => same.Count() > 1)
			.Select(same => same.Key);

		foreach (string areaId in duplicates)
			result.AddError($"Two rooms name area '{areaId}'. One room per Area: remove the second.");

		foreach (AreaConfig area in config.Areas)
		{
			ValidateSettings(area.DisplayName, area.Effective(config.Defaults), result);
			ValidateReferences(area, known, result);
			ValidateNeighbour(config, area, result);
			ValidateLevelRows(config.Periods, $"[{area.DisplayName}]", area.PeriodTemperatures, result);
		}
	}

	/// <summary>The rows one room states.</summary>
	/// <remarks>
	///     A dangling period id warns and the row survives: it is nearly always a period deleted by hand, and the levels
	///     are worth more than the tidiness. A value outside the physical range is an error, as the schedule's own is.
	/// </remarks>
	private static void ValidateLevelRows(
		List<TimePeriodConfig> periods,
		string scope,
		IReadOnlyList<RoomPeriodTemperature>? levels,
		ValidationResult result)
	{
		HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

		foreach (RoomPeriodTemperature level in levels ?? [])
		{
			ValidateLevelRange(periods, scope, level, result);

			if (level.PeriodId is not { Length: > 0 } key || string.IsNullOrWhiteSpace(key))
			{
				result.AddWarning(
					$"{scope} has a levels row naming no period, so it replaces nothing. Name the period "
					+ "it was written for, or remove the row.");
				continue;
			}

			if (level.IsEmpty)
				continue;

			// First wins, matching the calculator.
			if (!seen.Add(key.Trim()))
			{
				result.AddWarning(
					$"{scope} has more than one levels row for period '{ValidationRanges.PeriodLabel(periods, key)}'; the "
					+ "first one wins and the rest are ignored. Merge them into one row.");
				continue;
			}

			if (ValidationRanges.PeriodWithKey(periods, key) is null)
			{
				result.AddWarning(
					$"{scope} has levels for period '{key.Trim()}', which matches no configured period — "
					+ "almost always a period that has been deleted. The row is kept so the levels are not lost, but it "
					+ "does nothing until it names a period that exists.");
			}
		}
	}

	/// <summary>The physical range, checked whether or not the row's period resolves.</summary>
	private static void ValidateLevelRange(
		List<TimePeriodConfig> periods,
		string scope,
		RoomPeriodTemperature level,
		ValidationResult result)
	{
		string label = ValidationRanges.PeriodLabel(periods, level.PeriodId);

		if (level.Temperature is { } temperature && temperature is < ValidationRanges.MinTemperature or > ValidationRanges.MaxTemperature)
			result.AddError(
				$"{scope} levels for period '{label}' have Temperature {temperature}, outside {ValidationRanges.MinTemperature}–{ValidationRanges.MaxTemperature}.");
	}

	/// <summary>One settings block, whether it is the house defaults or one room's effective settings.</summary>
	internal static void ValidateSettings(string scope, AreaSettings settings, ValidationResult result)
	{
		if (settings.OverrideDurationMinutes < 0)
			result.AddError($"[{scope}] OverrideDurationMinutes must not be negative (is {settings.OverrideDurationMinutes}).");

		if (settings.LowestNightTemperature is < ValidationRanges.MinFrostTemperature or > ValidationRanges.MaxTemperature)
			result.AddError(
				$"[{scope}] LowestNightTemperature {settings.LowestNightTemperature} is outside {ValidationRanges.MinFrostTemperature}–{ValidationRanges.MaxTemperature}.");

		if (settings.FrostTemperature is < ValidationRanges.MinFrostTemperature or > ValidationRanges.MaxTemperature)
			result.AddError(
				$"[{scope}] FrostTemperature {settings.FrostTemperature} is outside {ValidationRanges.MinFrostTemperature}–{ValidationRanges.MaxTemperature}.");
	}

	/// <summary>A borrowed reading comes from another room that reads for itself, and from nowhere else.</summary>
	private static void ValidateNeighbour(HeatingConfig config, AreaConfig area, ValidationResult result)
	{
		if (area.Neighbour is not { Length: > 0 } neighbour)
			return;

		if (string.Equals(neighbour, area.AreaId, StringComparison.Ordinal))
		{
			result.AddAreaError(area.DisplayName, "Borrows its reading from itself. Pick another room, or none.");
			return;
		}

		AreaConfig? lender = config.Areas.FirstOrDefault(other => string.Equals(other.AreaId, neighbour, StringComparison.Ordinal));

		if (lender is null)
			result.AddAreaError(area.DisplayName, $"Borrows its reading from '{neighbour}', which is not a room. Pick another room, or none.");
		else if (lender.Neighbour is { Length: > 0 })
			result.AddAreaError(area.DisplayName, $"Borrows its reading from {lender.DisplayName}, which borrows its own. Pick a room with a sensor.");
	}

	private static void ValidateReferences(AreaConfig area, ValidationContext known, ValidationResult result)
	{
		bool hasNamedHeaters = area.Heaters is { Count: > 0 };

		if (string.IsNullOrWhiteSpace(area.AreaId) && !hasNamedHeaters)
		{
			result.AddAreaError(
				area.DisplayName,
				"Names no Home Assistant area and no heaters, so nothing can be found for it. Pick an area, or name its heaters by hand.");

			return;
		}

		if (known.KnownAreaIds is { } areaIds && area.AreaId is { Length: > 0 } areaId && !areaIds.Contains(areaId))
			result.AddAreaError(
				area.DisplayName,
				$"AreaId '{areaId}' is not a registry area id. AreaId is the slug, not the display name. Known area ids: {string.Join(", ", areaIds.Order(StringComparer.Ordinal))}.");

		ValidateHeatersAreNotOurs(area, known, result);

		if (known.KnownEntityIds is not { } entityIds)
			return;

		foreach (string entityId in EnumerateAreaEntities(area))
			if (!entityIds.Contains(entityId))
				result.AddAreaError(area.DisplayName, $"Names '{entityId}', which Home Assistant does not know. Check it for typos, or remove it.");
	}

	/// <summary>A room cannot name itself as its heater: this integration's own thermostat is a room.</summary>
	// Discovery already keeps them out, and a hand-picked list bypasses discovery, so this is the only gate on
	// that path. An error, not a warning: driving a room's own thermostat from the same room is a loop.
	private static void ValidateHeatersAreNotOurs(AreaConfig area, ValidationContext known, ValidationResult result)
	{
		if (known.OurOwnEntityIds is not { } ours || area.Heaters is not { Count: > 0 } heaters)
			return;

		foreach (string heater in heaters.Where(ours.Contains))
			result.AddAreaError(
				area.DisplayName,
				$"Names '{heater}' as a heater, which is this room's own thermostat. Name the heater it switches instead.");
	}

	private static IEnumerable<string> EnumerateAreaEntities(AreaConfig area)
	{
		foreach (string heater in area.Heaters ?? [])
			yield return heater;

		foreach (string sensor in area.TemperatureSensors ?? [])
			yield return sensor;

		if (area.WindowSensor is { Length: > 0 } window)
			yield return window;

		if (area.PowerSensor is { Length: > 0 } power)
			yield return power;

		foreach (string gate in (area.PauseWhenOn ?? []).Concat(area.HoldWhenOn ?? []))
			yield return gate;
	}
}
