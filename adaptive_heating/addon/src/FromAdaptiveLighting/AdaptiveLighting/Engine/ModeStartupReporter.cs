using AdaptiveLighting.Configuration;

namespace Adaptive.Engine;

/// <summary>What the mode brain says about itself once, at start-up: the rules that cannot fire and why.</summary>
// Silence here sends somebody hunting an automation that is working as configured, so each dormant or unreachable
// rule gets its own line, and only when the document actually carries that rule.
internal static class ModeStartupReporter
{
	/// <summary>Says so when the document leaves every away behaviour out of reach.</summary>
	// The commissioning sheet still offers the two room settings, and the validator warns on a save; a household
	// that never opens either would otherwise have no way to find out why leaving does nothing.
	internal static void AnnounceUnreachableAway(ILogger logger, GlobalConfig global)
	{
		if (global.HouseMode is not { Options.Count: > 0 } houseMode || houseMode.HasAwayOption)
			return;

		logger.LogWarning(
			"No house-mode option is marked Away, so this house can never be away: the leaving sweep never runs, "
			+ "an away scene never fires, and every room's 'stays on when the house goes away' and 'lights up when "
			+ "the house leaves away mode' setting is inert.");
	}

	/// <summary>Names every mode rule Home Assistant's authority has stood down.</summary>
	internal static void AnnounceDormantModeRules(
		ILogger logger,
		GlobalConfig global,
		IReadOnlyList<TimePeriodConfig> periods,
		bool houseModeIsHomeAssistants)
	{
		if (!houseModeIsHomeAssistants || global.HouseMode is not { } houseMode)
			return;

		logger.LogInformation(
			"House mode: Home Assistant decides. The engine reads {Select} and never writes it.", houseMode.EntityId);

		if (periods.Any(period => period.SetsModeId is { Length: > 0 }))
			logger.LogInformation(
				"A period switching the house mode is dormant while Home Assistant decides it; the schedule will not move {Select}.",
				houseMode.EntityId);

		if (houseMode.Options.Any(option => option.Kind != ModeKind.Normal && option.ActivateAfterNoMotionMinutes is > 0))
			logger.LogInformation(
				"The no-motion auto-away rule is dormant while Home Assistant decides the house mode.");

		if (houseMode.Options.Any(option => option.ActivateWhileOn.Count > 0))
			logger.LogInformation(
				"ActivateWhileOn is dormant while Home Assistant decides the house mode; the select's own value is the whole story.");

		// Kind-filtered, as the presence resets and period entry both are: a Normal option's reset never fired
		// under either authority, so naming it here reports a rule that was not switched off.
		if (houseMode.Options.Any(option => option.Kind != ModeKind.Normal && option.HasResetTrigger))
			logger.LogInformation(
				"The mode resets are dormant while Home Assistant decides the house mode; nothing here returns {Select} to Normal.",
				houseMode.EntityId);
	}

	/// <summary>Says that the two motion rules can never fire, because no area motion sensor resolves.</summary>
	internal static void AnnounceMotionCannotFire(ILogger logger, bool autoAway, bool startsPeriods)
	{
		if (autoAway)
			logger.LogWarning("An option activates on no motion, but no area motion sensors resolve; it can never fire.");

		if (startsPeriods)
			logger.LogWarning("A period starts on motion, but no area motion sensors resolve; it can never fire.");
	}
}
