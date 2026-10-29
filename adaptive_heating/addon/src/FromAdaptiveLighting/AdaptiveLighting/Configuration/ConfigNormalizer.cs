using AdaptiveLighting.Configuration;

namespace Adaptive.Settings;

/// <summary>The save-time normaliser: trims what a person typed and drops a blank once it is provably redundant.</summary>
/// <remarks>Runs on write only, before validation. The load path must never rewrite a hand-edited file.</remarks>
public static class ConfigNormalizer
{
	/// <summary>Mutates and returns <paramref name="config"/> in place; the drops are the intended change.</summary>
	public static HeatingConfig Normalize(HeatingConfig config)
	{
		ArgumentNullException.ThrowIfNull(config);

		GlobalConfig global = config.Global;

		// Before anything that reads a key: a row added since the last save has no id yet.
		StableKeyMigration.Apply(config);

		// A row a period's SetsModeId names is kept, or the validator refuses the save it came from.
		if (global.HouseMode is { } mode)
		{
			HouseModeOptionConfig? normal = mode.NormalOption;
			HashSet<string> referenced = config.Periods
				.Where(period => period.SetsModeId is { Length: > 0 })
				.Select(period => period.SetsModeId!.Trim())
				.ToHashSet(StringComparer.OrdinalIgnoreCase);

			mode.Options.RemoveAll(option =>
				!ReferenceEquals(option, normal)
				&& IsPureDefault(option)
				&& !referenced.Contains(option.Key));
		}

		// Trimmed but never dropped for being unreadable: the validator warns about a typo, and silently deleting
		// the line the operator is halfway through typing would hide it.
		for (int i = 0; i < global.EmbedFrom.Count; i++)
			global.EmbedFrom[i] = global.EmbedFrom[i]?.Trim() ?? string.Empty;

		global.EmbedFrom.RemoveAll(string.IsNullOrEmpty);

		// Drop an empty HouseMode so a never-adopted document acquires no HouseMode: block.
		if (global.HouseMode is { } houseMode
			&& string.IsNullOrWhiteSpace(houseMode.Entity)
			&& houseMode.Options.Count == 0)
			global.HouseMode = null;

		// Two steps, so a block that held only cleared rows can then be recognised as empty and dropped too.
		if (global.PeriodSelect is { } periodSelect)
		{
			periodSelect.Options.RemoveAll(option => option.IsEmpty);

			// Authority means nothing without an entity, so it is not consulted.
			if (string.IsNullOrWhiteSpace(periodSelect.Entity) && periodSelect.Options.Count == 0)
				global.PeriodSelect = null;
		}

		// Null and not an empty string: both read as unset, and only null is omitted by the serialiser, so an
		// emptied field would put its key back into every document.
		global.GateEntity = Blank(global.GateEntity);
		global.OutdoorSensor = Blank(global.OutdoorSensor);
		global.ForecastEntity = Blank(global.ForecastEntity);
		global.ArrivalCalendar = Blank(global.ArrivalCalendar);
		global.RecordCalendar = Blank(global.RecordCalendar);
		global.IncludeLabel = Blank(global.IncludeLabel);
		global.LogLevel = Blank(global.LogLevel);

		// Never null: the exclude label has a default, and clearing it on the page means "take the default back".
		global.ExcludeLabel = Blank(global.ExcludeLabel) ?? GlobalConfig.DefaultExcludeLabel;

		foreach (AreaConfig area in config.Areas)
		{
			area.AreaId = area.AreaId?.Trim() ?? "";
			area.Name = Blank(area.Name);

			area.ExcludeEntities = Trimmed(area.ExcludeEntities);
		}

		foreach (TimePeriodConfig period in config.Periods)
		{
			period.Id = period.Id?.Trim() ?? "";
			period.Name = period.Name?.Trim() ?? "";
			period.Start = period.Start?.Trim() ?? "";
		}

		return config;
	}

	private static bool IsPureDefault(HouseModeOptionConfig option) =>
		option.Kind == ModeKind.Normal
		&& string.IsNullOrWhiteSpace(option.ClampPeriodId)
		&& !option.NightSetback
		&& option.ActivateWhileOn.Count == 0
		&& option.ActivateAfterNoMotionMinutes is null
		&& !option.HasResetTrigger;

	private static string? Blank(string? value) =>
		value is { Length: > 0 } && value.Trim() is { Length: > 0 } trimmed ? trimmed : null;

	/// <summary>The list with every entry trimmed and every blank gone, or <c>null</c> once nothing is left.</summary>
	private static List<string>? Trimmed(List<string>? entries)
	{
		if (entries is null)
			return null;

		List<string> kept =
		[
			.. entries
				.Where(entry => !string.IsNullOrWhiteSpace(entry))
				.Select(entry => entry.Trim())
				.Distinct(StringComparer.Ordinal)
		];

		return kept.Count > 0 ? kept : null;
	}
}
