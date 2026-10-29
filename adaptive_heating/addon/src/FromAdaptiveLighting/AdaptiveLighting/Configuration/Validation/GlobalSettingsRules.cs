using Adaptive.Engine;

using AdaptiveLighting.Configuration;

namespace Adaptive.Settings;

/// <summary>The house-wide settings: the numbers, the entities they name, the labels, and the warm-up source.</summary>
internal static class GlobalSettingsRules
{
	internal static void Validate(GlobalConfig global, ValidationContext context, ValidationResult result)
	{
		IReadOnlyCollection<string>? knownEntityIds = context.KnownEntityIds;

		ValidateIncludeLabel(global, context.LabelsInUse, result);

		if (global.AwayDebounceMinutes < 0)
			result.AddError($"Global.AwayDebounceMinutes must not be negative (is {global.AwayDebounceMinutes}).");

		if (global.PassCadenceSeconds <= 0)
			result.AddError($"Global.PassCadenceSeconds must be positive (is {global.PassCadenceSeconds}).");

		if (global.SelfEchoWindowSeconds < 0)
			result.AddError($"Global.SelfEchoWindowSeconds must not be negative (is {global.SelfEchoWindowSeconds}).");

		if (string.IsNullOrWhiteSpace(global.SunEntity))
			result.AddError("Global.SunEntity is empty.");

		ValidateEmbedFrom(global, result);

		// A warning: an unrecognised word must never stop a house starting.
		if (global.LogLevel is { Length: > 0 } logLevel && LogLevelSetting.Read(logLevel) is null)
			result.AddWarning(
				$"Global.LogLevel '{logLevel}' is not one of {string.Join(", ", LogLevelSetting.Accepted)}, so the host's own level stays in force.");

		if (knownEntityIds is null)
			return;

		foreach ((string? label, string? entityId) in EnumerateGlobalEntities(global))
			if (!knownEntityIds.Contains(entityId))
				result.AddError($"Global.{label} refers to '{entityId}', which Home Assistant does not know.");

		// Never an error: the engine fails open on an unreadable gate, so a missing one cannot leave a cabin cold,
		// and the built-in switch may simply not have been created in Home Assistant yet.
		if (global.EffectiveGateEntity(context.DefaultGateEntity) is { Length: > 0 } gate
			&& !knownEntityIds.Contains(gate))
		{
			if (global.GateEntity is { Length: > 0 })
				result.AddWarning($"Global.GateEntity refers to '{gate}', which Home Assistant does not know — the engine runs ungated (it fails open on a missing switch). Clear it to fall back to the built-in switch.");
			else
				result.AddWarning($"The built-in switch '{gate}' is not known to Home Assistant yet; create it as an input_boolean helper to be able to stop the heating from a dashboard.");
		}

		// Warnings, not errors: the house still runs on the rules that do not need the sun, and refusing the save
		// would block the page that fixes the id.
		if (global.SunEntity is { Length: > 0 } sun)
		{
			if (sun.Domain() is not "sun")
				result.AddWarning($"Global.SunEntity '{sun}' is not a sun entity; every sun-anchored period boundary has nothing to read.");
			else if (!knownEntityIds.Contains(sun))
				result.AddWarning($"Global.SunEntity '{sun}' is not known to Home Assistant; every sun-anchored period boundary has nothing to read until it appears.");
		}

		// Fails open: an unknown or non-sensor id leaves every room with no outdoor reading, which is the case the
		// staleness rule already covers, so refusing the save would block the page that fixes the id.
		if (global.OutdoorSensor is { Length: > 0 } outdoor)
		{
			if (outdoor.Domain() is not "sensor")
				result.AddWarning($"Global.OutdoorSensor '{outdoor}' is not a sensor entity; every room runs without an outdoor reading.");
			else if (!knownEntityIds.Contains(outdoor))
				result.AddWarning($"Global.OutdoorSensor '{outdoor}' is not known to Home Assistant; every room runs without an outdoor reading until it appears.");
		}

		if (global.ForecastEntity is { Length: > 0 } forecast)
		{
			if (forecast.Domain() is not "weather")
				result.AddWarning($"Global.ForecastEntity '{forecast}' is not a weather entity; no forecast can be read from it.");
			else if (!knownEntityIds.Contains(forecast))
				result.AddWarning($"Global.ForecastEntity '{forecast}' is not known to Home Assistant; no forecast can be read until it appears.");
		}
	}

	/// <summary>The warm-up source: one of the two values, and a warning where the forecast has no entity.</summary>
	/// <remarks>
	///     Never an error: a document from the defaults names the forecast and no entity, and refusing it would leave
	///     a fresh install unable to save. The warm-up plans on the outdoor measurement until an entity is named.
	/// </remarks>
	internal static void ValidateWarmUpSource(GlobalConfig global, ValidationResult result)
	{
		// An unknown enum key is silence in the binder, but a bare numeral binds, so the value can be out of range.
		if (!Enum.IsDefined(global.WarmUpSource))
		{
			result.AddError(
				$"Global.WarmUpSource is '{(int)global.WarmUpSource}', which is not one of "
				+ $"{nameof(WarmUpSource.Forecast)} or {nameof(WarmUpSource.OutdoorMeasurement)}.");

			return;
		}

		if (global.WarmUpSource is WarmUpSource.Forecast && global.ForecastEntity is not { Length: > 0 })
			result.AddWarning("The forecast has no entity.");
	}

	// A warning: the unreadable entry is left out of the header, and refusing the save would block the fix.
	private static void ValidateEmbedFrom(GlobalConfig global, ValidationResult result)
	{
		for (int i = 0; i < global.EmbedFrom.Count; i++)
		{
			string entry = global.EmbedFrom[i];

			if (EmbedOrigin.TryRead(entry, out _))
				continue;

			result.AddWarning(string.IsNullOrWhiteSpace(entry)
				? $"Global.EmbedFrom has an empty address at position {i + 1}; remove it."
				: $"Global.EmbedFrom entry '{entry}' is not an address a browser can match, so it is left out. Write the scheme, host and port only, as in http://10.0.0.5:8123.");
		}
	}

	/// <summary>A label setting holding no label id the house knows, which matches nothing.</summary>
	internal static void ValidateLabelsAreIds(
		GlobalConfig global,
		IReadOnlyCollection<string>? knownLabelIds,
		ValidationResult result)
	{
		foreach ((string setting, string value) in LabelTranslation.NotIds(global, knownLabelIds))
			result.AddWarning(
				$"Global.{setting} is '{value}', which Home Assistant does not know as a label id, so it matches nothing. "
				+ "Pick the label again on the settings page.");
	}

	/// <summary>The include label, when nothing in Home Assistant carries it.</summary>
	/// <remarks>The filter fails closed room by room, so this says once that one typo at the top of the file is behind every per-room message.</remarks>
	private static void ValidateIncludeLabel(
		GlobalConfig global,
		IReadOnlyCollection<string>? labelsInUse,
		ValidationResult result)
	{
		if (labelsInUse is null || global.IncludeLabel is not { Length: > 0 } include)
			return;

		if (!labelsInUse.Contains(include, StringComparer.OrdinalIgnoreCase))
			result.AddWarning(
				$"Global.IncludeLabel is '{include}', which nothing in Home Assistant carries — with it set and unmatched, "
				+ "no room finds a heater to manage. Clear it to manage every heater discovery finds, or label the heaters in Home Assistant.");
	}

	private static IEnumerable<(string Label, string EntityId)> EnumerateGlobalEntities(GlobalConfig global)
	{
		foreach (string person in global.Persons)
			yield return (nameof(GlobalConfig.Persons), person);

		if (global.HouseMode?.Entity is { Length: > 0 } houseMode)
			yield return ($"{nameof(GlobalConfig.HouseMode)}.{nameof(HouseModeConfig.Entity)}", houseMode);
	}
}
