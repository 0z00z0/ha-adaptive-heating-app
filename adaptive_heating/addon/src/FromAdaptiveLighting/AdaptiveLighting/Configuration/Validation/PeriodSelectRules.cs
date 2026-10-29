using Adaptive.Engine;

using AdaptiveLighting.Configuration;

namespace Adaptive.Settings;

/// <summary>The <c>input_select</c> tied to the period table, in whichever direction its authority names.</summary>
/// <remarks>
///     An unresolvable mapping is an error here where the same shape is a warning on a room's levels: under
///     <see cref="PeriodAuthority.HomeAssistant"/> it costs every room at once. A stored value the live select no
///     longer offers stays a warning, since erroring would make the document unsaveable from the page that fixes it.
/// </remarks>
internal static class PeriodSelectRules
{
	internal static void Validate(
		HeatingConfig config,
		IReadOnlyCollection<string>? knownEntityIds,
		IReadOnlyCollection<string>? livePeriodSelectOptions,
		ValidationResult result)
	{
		if (config.Global.PeriodSelect is not { } select)
			return;

		// Mappings but no entity. Every other branch below is skipped in that case, so without this the household is
		// told nothing at all, and the normaliser cannot drop a block that still holds rows.
		if (string.IsNullOrWhiteSpace(select.Entity) && select.Options.Count > 0)
		{
			result.AddWarning(
				$"[PeriodSelect] maps {select.Options.Count} option(s) to periods but names no Entity, so nothing "
				+ "reads or writes them and the schedule stays in charge. Name the input_select, or remove the block.");
		}

		if (select.Entity is { Length: > 0 } entity)
		{
			// One helper cannot be both the house mode and the time of day. Both are input_selects, so every other
			// rule passes; under AdaptiveLighting authority the period mirror would then overwrite Away or Sleep
			// within one tick of it being set. The per-object authority check cannot see two objects on one helper.
			if (config.Global.HouseMode?.Entity is { Length: > 0 } houseModeEntity
				&& entity.SameName(houseModeEntity))
			{
				result.AddError(
					$"[PeriodSelect] Entity '{entity}' is also the house-mode select. One helper cannot carry both "
					+ "the house mode and the time of day: the two would overwrite each other every tick. Give the "
					+ "periods their own input_select.");
			}

			// Wrong domain can never work, so it errors. An unknown id fails open in both directions and only warns.
			if (!entity.HasDomain("input_select"))
				result.AddError($"PeriodSelect.Entity '{entity}' is not an input_select. The time of day is a Home Assistant dropdown helper.");
			else if (knownEntityIds is not null && !knownEntityIds.Contains(entity))
				result.AddWarning($"PeriodSelect.Entity '{entity}' is not known to Home Assistant; until it appears, every room follows the schedule.");
		}

		HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

		foreach (PeriodSelectOptionConfig option in select.Options)
		{
			if (string.IsNullOrWhiteSpace(option.Value))
			{
				result.AddError("A PeriodSelect option has a blank Value, so no select option can ever match it.");
				continue;
			}

			// Only the first row for an option string is ever read.
			if (!seen.Add(option.Value.Trim()))
				result.AddError($"Duplicate PeriodSelect option value '{option.Value.Trim()}'.");

			if (string.IsNullOrWhiteSpace(option.PeriodId))
				result.AddError($"PeriodSelect option '{option.Value.Trim()}' names no period, so selecting it would mean nothing.");
			else if (ValidationRanges.PeriodWithKey(config.Periods, option.PeriodId) is null)
				result.AddError($"PeriodSelect option '{option.Value.Trim()}' maps to period '{option.PeriodId.Trim()}', which matches no configured period.");
		}

		// Authority is Home Assistant's with nothing to decide with. The engine falls back to its own schedule for
		// every unmapped value, so this warns instead of erroring.
		if (select.Authority is PeriodAuthority.HomeAssistant && select.Options.Count == 0)
			result.AddWarning(
				"PeriodSelect.Authority is HomeAssistant but no option is mapped to a period, so the select can never "
				+ "change the time of day and every room keeps following the schedule. Map its options, or set "
				+ "Authority back to AdaptiveLighting.");

		if (livePeriodSelectOptions is null)
			return;

		foreach (PeriodSelectOptionConfig option in select.Options.Where(o => !string.IsNullOrWhiteSpace(o.Value)))
			if (!livePeriodSelectOptions.Any(live => live.SameName(option.Value)))
				result.AddWarning(
					$"PeriodSelect option {HelperOrphan.NoLongerOffered(option.Value)} — until it matches one again "
					+ "the mapping does nothing.");
	}
}
