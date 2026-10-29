using Adaptive.Engine;

using AdaptiveLighting.Configuration;

namespace Adaptive.Settings;

/// <summary>The house-mode rules: structural problems are document-level errors, classification quirks warnings.</summary>
// Named for the section rather than for the house mode alone: AdaptiveLighting.Engine already has an internal
// HouseModeRules, and the two would be ambiguous wherever both namespaces are in scope.
internal static class HouseModeSectionRules
{
	internal static void Validate(
		HeatingConfig config,
		IReadOnlyCollection<string>? knownEntityIds,
		IReadOnlyCollection<string>? liveSelectOptions,
		ValidationResult result)
	{
		HouseModeConfig? houseMode = config.Global.HouseMode;
		List<TimePeriodConfig> periods = config.Periods;

		foreach (TimePeriodConfig? period in periods.Where(p => p.SetsModeId is { Length: > 0 }))
		{
			string setsMode = period.SetsModeId!;
			HouseModeOptionConfig? option = houseMode?.OptionWithKey(setsMode);

			// A live option the owner has not tagged yet is still legitimate, and has no id to be named by, so the
			// raw value is still matched. Erroring on it would deadlock the save, because tagging it is itself a save.
			bool isLiveOption = liveSelectOptions?.Any(live => live.SameName(setsMode)) ?? false;

			if (option is null && !isLiveOption)
				result.AddError($"Period '{period.Name}' switches the house mode to '{setsMode}', which matches no house-mode option — neither a configured one nor a live option of the select.");
			else if (option?.Kind == ModeKind.Normal)
				result.AddWarning($"Period '{period.Name}' switches the house mode to '{option.Value}', which is a Normal option — the period would schedule a reset to the baseline.");
		}

		ValidateSleepPath(config, houseMode, result);

		if (houseMode is not null)
			ValidateAuthority(config, houseMode, result);

		if (houseMode?.Entity is not { Length: > 0 })
			return;

		// The "unknown to HA" half of the entity check is in GlobalSettingsRules.
		if (!houseMode.Entity.HasDomain("input_select"))
			result.AddError($"HouseMode.Entity '{houseMode.Entity}' is not an input_select. The house mode is a Home Assistant dropdown helper.");

		foreach (HouseModeOptionConfig option in houseMode.Options)
			if (string.IsNullOrWhiteSpace(option.Value))
				result.AddError("A HouseMode option has a blank Value.");

		IEnumerable<string> duplicateOptions = houseMode.Options
			.Where(o => !string.IsNullOrWhiteSpace(o.Value))
			.GroupBy(o => o.Value.Trim(), StringComparer.OrdinalIgnoreCase)
			.Where(g => g.Count() > 1)
			.Select(g => g.Key);

		foreach (string? value in duplicateOptions)
			result.AddError($"Duplicate HouseMode option value '{value}'.");

		ValidateNormalCount(houseMode, result);
		ValidateAwayReachable(houseMode, result);

		foreach (HouseModeOptionConfig? option in houseMode.Options.Where(o => !string.IsNullOrWhiteSpace(o.Value)))
			ValidateOption(config, option, knownEntityIds, result);

		if (liveSelectOptions is not null)
			WarnOnLiveOptionMismatch(houseMode, liveSelectOptions, result);
	}

	/// <summary>Home Assistant's authority stands the engine's own mode rules down; this names the ones gone quiet.</summary>
	/// <remarks>
	///     Warnings throughout. Each rule degrades to one that no longer fires, and an error would block the save from
	///     the page that hands the authority back.
	/// </remarks>
	private static void ValidateAuthority(
		HeatingConfig config,
		HouseModeConfig houseMode,
		ValidationResult result)
	{
		if (houseMode.Authority is not HouseModeAuthority.HomeAssistant)
			return;

		// HomeAssistantDecides is false without an entity, so the engine still decides and nothing below is dormant.
		if (!houseMode.HomeAssistantDecides)
		{
			result.AddWarning(
				"HouseMode.Authority is HomeAssistant but no Entity is named, so there is no dropdown to read and this "
				+ "application keeps deciding the mode. Name the input_select, or set Authority back to AdaptiveLighting.");
			return;
		}

		List<string> setsMode = [.. config.Periods
			.Where(period => period.SetsModeId is { Length: > 0 })
			.Select(period => $"'{period.Name}'")];

		if (setsMode.Count > 0)
			result.AddWarning(
				$"HouseMode.Authority is HomeAssistant, so the mode switch on period(s) {string.Join(", ", setsMode)} is "
				+ "dormant: the engine reads the select and never writes it. Clear it, or set Authority back to "
				+ "AdaptiveLighting.");

		foreach (HouseModeOptionConfig option in houseMode.Options)
		{
			// The same predicates ModeMonitor stands down on, member for member. A rule that was never live under
			// either authority is not dormant, and saying it is sends somebody looking for an automation that
			// never ran: Normal is exempt from both no-motion and reset, and a zero minute count arms nothing.
			if (option.Kind != ModeKind.Normal && option.ActivateAfterNoMotionMinutes is > 0)
				result.AddWarning(
					$"HouseMode option '{option.Value}' sets ActivateAfterNoMotionMinutes, which is dormant while "
					+ "HouseMode.Authority is HomeAssistant — only the dropdown moves the house.");

			if (option.ActivateWhileOn.Count > 0)
				result.AddWarning(
					$"HouseMode option '{option.Value}' lists ActivateWhileOn entities, which are dormant while "
					+ "HouseMode.Authority is HomeAssistant — only the dropdown moves the house.");

			// The fourth rule. A reset writes the select back to Normal, so it stands down with the rest.
			// HasResetTrigger, not the sensor list: presence is gated on ResetOnPresence alone, so sensors listed
			// with the toggle off are inert and the toggle on with no sensors is live.
			if (option.Kind != ModeKind.Normal && option.HasResetTrigger)
				result.AddWarning(
					$"HouseMode option '{option.Value}' carries a reset trigger, which is dormant while "
					+ "HouseMode.Authority is HomeAssistant — a reset writes the select, and the engine never does.");
		}
	}

	/// <summary>One Normal, no more. With none the first option is treated as Normal; with several the first wins.</summary>
	private static void ValidateNormalCount(HouseModeConfig houseMode, ValidationResult result)
	{
		List<HouseModeOptionConfig> configured = houseMode.Options.Where(o => !string.IsNullOrWhiteSpace(o.Value)).ToList();
		if (configured.Count == 0)
			return;

		int normals = configured.Count(o => o.Kind == ModeKind.Normal);
		if (normals == 0)
			result.AddWarning($"No option is marked Normal — '{configured[0].Value}' is being treated as Normal (the reset target). Pick one explicitly.");
		else if (normals > 1)
			result.AddWarning("More than one option is marked Normal; the first wins as the reset target.");
	}

	/// <summary>Warns when nothing is marked Away, which makes every away behaviour unreachable.</summary>
	// A warning, never an error: such a house runs perfectly well, it simply never goes away.
	private static void ValidateAwayReachable(HouseModeConfig houseMode, ValidationResult result)
	{
		if (houseMode.Options.All(o => string.IsNullOrWhiteSpace(o.Value)) || houseMode.HasAwayOption)
			return;

		result.AddWarning(
			"No option is marked Away, so this house can never be away. Mark the option the household uses for leaving as Away.");
	}

	/// <summary>Per-option rules: reset triggers, and reset fields set on a Normal option.</summary>
	private static void ValidateOption(
		HeatingConfig config,
		HouseModeOptionConfig option,
		IReadOnlyCollection<string>? knownEntityIds,
		ValidationResult result)
	{
		bool isNormal = option.Kind == ModeKind.Normal;
		bool isAwayOrGuest = option.Kind is ModeKind.Away or ModeKind.Guest;

		// ClampPeriodId is load-bearing only on Sleep, so a dangling id errors only there.
		if (option.ClampPeriodId is { Length: > 0 } clamp)
		{
			if (option.Kind != ModeKind.Sleep)
				result.AddWarning($"HouseMode option '{option.Value}' has a ClampPeriodId but its kind is {option.Kind}; it is inert.");
			else if (ValidationRanges.PeriodWithKey(config.Periods, clamp) is null)
				result.AddError($"HouseMode option '{option.Value}' ClampPeriodId '{clamp.Trim()}' matches no configured period.");
		}

		if (option.NightSetback && option.Kind != ModeKind.Sleep)
			result.AddWarning($"HouseMode option '{option.Value}' sets rooms back for the night but its kind is {option.Kind}; it is inert.");

		// Normal is the reset target, so a trigger on it is inert.
		if (isNormal && option.HasResetTrigger)
			result.AddWarning($"HouseMode option '{option.Value}' is Normal but carries reset triggers; they are inert on the reset target.");

		if (isAwayOrGuest && !option.HasResetTrigger)
			result.AddWarning($"HouseMode option '{option.Value}' is {option.Kind} but has no reset trigger; it will stay active until a manual change.");

		if (option.ResetOnPeriodStartId is { Length: > 0 } resetPeriod
			&& ValidationRanges.PeriodWithKey(config.Periods, resetPeriod) is null)
		{
			if (isNormal)
				result.AddWarning($"HouseMode option '{option.Value}' ResetOnPeriodStartId '{resetPeriod.Trim()}' matches no period, but the option is Normal so it is inert.");
			else
				result.AddError($"HouseMode option '{option.Value}' ResetOnPeriodStartId '{resetPeriod.Trim()}' matches no configured period.");
		}

		if (option.ResetPresenceGraceMinutes < 0)
			result.AddError($"HouseMode option '{option.Value}' ResetPresenceGraceMinutes must not be negative (is {option.ResetPresenceGraceMinutes}).");

		// Zero would fire the instant motion stops.
		if (option.ActivateAfterNoMotionMinutes is { } idleMinutes)
		{
			if (idleMinutes <= 0)
				result.AddError($"HouseMode option '{option.Value}' ActivateAfterNoMotionMinutes must be positive (is {idleMinutes}).");
			else if (isNormal)
				result.AddWarning($"HouseMode option '{option.Value}' is Normal but sets ActivateAfterNoMotionMinutes; a Normal option is the reset target, so it is inert.");
		}

		// Read as on/off, so only input_boolean, switch and binary_sensor can force the mode.
		foreach (string? sensor in option.ActivateWhileOn.Where(s => !string.IsNullOrWhiteSpace(s)))
		{
			if (sensor.Domain() is not ("input_boolean" or "switch" or "binary_sensor"))
				result.AddWarning($"HouseMode option '{option.Value}' ActivateWhileOn includes '{sensor}', which is not an input_boolean, switch or binary_sensor; it cannot turn the mode on.");

			if (knownEntityIds is not null && !knownEntityIds.Contains(sensor))
				result.AddError($"HouseMode option '{option.Value}' ActivateWhileOn refers to '{sensor}', which Home Assistant does not know.");
		}

		// Presence is detected on binary_sensor turn-on, or person/device_tracker moving to home. Nothing else.
		foreach (string? sensor in option.ResetPresenceSensors.Where(s => !string.IsNullOrWhiteSpace(s)))
		{
			if (sensor.Domain() is not ("binary_sensor" or "person" or "device_tracker"))
				result.AddWarning($"HouseMode option '{option.Value}' ResetPresenceSensors includes '{sensor}', which is not a binary_sensor, person or device_tracker; its presence will not reset the mode.");

			if (knownEntityIds is not null && !knownEntityIds.Contains(sensor))
				result.AddError($"HouseMode option '{option.Value}' ResetPresenceSensors refers to '{sensor}', which Home Assistant does not know.");
		}
	}

	/// <summary>When any option is Sleep, the clamp chain must resolve: every room obeys sleep.</summary>
	private static void ValidateSleepPath(HeatingConfig config, HouseModeConfig? houseMode, ValidationResult result)
	{
		List<HouseModeOptionConfig> sleepOptions = houseMode?.Options.Where(o => o.Kind == ModeKind.Sleep).ToList() ?? [];

		foreach (HouseModeOptionConfig? option in sleepOptions)
		{
			if (HouseModeConfig.SleepClampPeriodFor(option, config.Periods) is null)
				result.AddError(
					$"Sleep option '{option.Value}' has no period to hold the rooms at or below: " +
					"set its ClampPeriodId, have a period switch to this option, or add a period named 'night'.");
		}
	}

	private static void WarnOnLiveOptionMismatch(
		HouseModeConfig houseMode,
		IReadOnlyCollection<string> liveSelectOptions,
		ValidationResult result)
	{
		foreach (HouseModeOptionConfig option in houseMode.Options)
			if (option.Value is { Length: > 0 }
				&& !liveSelectOptions.Any(live => live.SameName(option.Value)))
				result.AddWarning(
					$"HouseMode option {HelperOrphan.NoLongerOffered(option.Value)} — its kind and reset triggers are inert.");

		foreach (string live in liveSelectOptions)
			if (live is { Length: > 0 } && houseMode.OptionFor(live) is null)
				result.AddWarning($"The select offers option '{live}', which nothing has classified — it behaves as Normal.");
	}
}
