namespace Adaptive.Settings;

/// <summary>
///     Checks a <see cref="HeatingConfig"/> before the engine is built. Pure: what Home Assistant knows is passed
///     in, never read, so the whole validator is unit-testable without fakes. Nothing is written to the document.
/// </summary>
/// <remarks>
///     Document-level problems stop the engine. Referential problems are one area's business: an entity renamed in
///     Home Assistant costs that area, not the house. The rules live one section per class beside this file; this is
///     the entry point and the order they run in.
/// </remarks>
public static class ConfigValidator
{
	/// <summary>Validates <paramref name="config"/> against <paramref name="context"/>, or against nothing known when it is <c>null</c>.</summary>
	public static ValidationResult Validate(HeatingConfig config, ValidationContext? context = null)
	{
		ArgumentNullException.ThrowIfNull(config);

		ValidationContext known = context ?? ValidationContext.None;
		ValidationResult result = new();

		GlobalSettingsRules.Validate(config.Global, known, result);
		GlobalSettingsRules.ValidateLabelsAreIds(config.Global, known.KnownLabelIds, result);
		GlobalSettingsRules.ValidateWarmUpSource(config.Global, result);
		PeriodRules.Validate(config.Periods, result);
		HouseModeSectionRules.Validate(config, known.KnownEntityIds, known.LiveSelectOptions, result);
		PeriodSelectRules.Validate(config, known.KnownEntityIds, known.LivePeriodSelectOptions, result);
		AreaRules.ValidateSettings("Defaults", config.Defaults, result);
		AreaRules.Validate(config, known, result);

		return result;
	}
}
