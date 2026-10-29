using Adaptive.Settings;

namespace Adaptive.Engine;

/// <summary>What one setup run will do, itemised so the warning dialog can be concrete about losses.</summary>
// A room discovery has stopped proposing is reported and never removed; removing a room stays the owner's act.
public sealed record SetupPlan(
	IReadOnlyList<AreaConfig> NewAreas,
	IReadOnlyList<AreaRebuildPlan> Rebuilds,
	IReadOnlyList<string> NoLongerQualifying)
{
	/// <summary>Areas Home Assistant reports that no room here names, each with why discovery leaves it out.</summary>
	public IReadOnlyList<SkippedArea> NotQualifying { get; init; } = [];
}

/// <summary>One Area a "set up rooms again" run will not add, and why.</summary>
public sealed record SkippedArea(string AreaId, string Reason);

/// <summary>One existing room's rebuild; the three counts are what a rebuild destroys.</summary>
public sealed record AreaRebuildPlan(string AreaId, int PinnedEntityCount, int OverrideCount, bool HasCustomName)
{
	/// <summary>Why the Area does not qualify, set only where a rebuild would drop the room.</summary>
	public string? SkipReason { get; init; }
}

/// <summary>Sets rooms up from what Home Assistant knows, on a first start and again whenever the owner asks.</summary>
// One behaviour, no merge parameter and no preserve-list, so the warning dialog is always true. Plan is pure:
// registry in, plan out, nothing written. Apply mutates the in-memory document only.
public static class AreaSetupService
{
	/// <summary>Works out what setting <paramref name="scope"/> up again would do, without touching anything.</summary>
	// scope is the area ids ticked for rebuild; ids the document lacks are ignored.
	public static SetupPlan Plan(
		HeatingConfig config,
		IAreaRegistry registry,
		AreaEntityResolver resolver,
		IReadOnlyCollection<string> scope)
	{
		ArgumentNullException.ThrowIfNull(config);
		ArgumentNullException.ThrowIfNull(registry);
		ArgumentNullException.ThrowIfNull(resolver);
		ArgumentNullException.ThrowIfNull(scope);

		IReadOnlyList<AreaConfig> proposed = AreaAutoDiscovery.Propose(registry, resolver);

		HashSet<string> qualifying = new(proposed.Select(area => area.AreaId!), StringComparer.Ordinal);
		HashSet<string> alreadyConfigured = new(AreaIdsOf(config), StringComparer.Ordinal);
		HashSet<string> ticked = new(scope, StringComparer.Ordinal);

		List<AreaConfig> newAreas = [.. proposed.Where(area => !alreadyConfigured.Contains(area.AreaId!))];

		List<AreaRebuildPlan> rebuilds = [];
		List<string> noLongerQualifying = [];

		foreach (AreaConfig area in config.Areas)
		{
			// A room with no area id has nothing for discovery to rebuild it from, so it can never be ticked.
			if (area.AreaId is not { Length: > 0 } areaId || !ticked.Contains(areaId))
				continue;

			// A ticked room is rebuilt whether or not its Area still qualifies, with no exception.
			string? skipReason = qualifying.Contains(areaId) ? null : resolver.ExplainWhyNotARoom(areaId);

			rebuilds.Add(new AreaRebuildPlan(
				areaId,
				PinnedEntityCount(area),
				OverrideCount(area),
				area.Name is { Length: > 0 })
			{
				SkipReason = skipReason
			});

			if (skipReason is not null)
				noLongerQualifying.Add(areaId);
		}

		// Every Area Home Assistant reports that is neither a room here nor about to become one, in a fixed order.
		List<SkippedArea> notQualifying = [.. registry.AreaIds
			.Where(id => !string.IsNullOrWhiteSpace(id))
			.Where(id => !alreadyConfigured.Contains(id) && !qualifying.Contains(id))
			.Order(StringComparer.Ordinal)
			.Select(id => new SkippedArea(id, resolver.ExplainWhyNotARoom(id)))];

		return new SetupPlan(newAreas, rebuilds, noLongerQualifying) { NotQualifying = notQualifying };
	}

	/// <summary>Carries <paramref name="plan"/> out on <paramref name="config"/>, in memory.</summary>
	// AreaId, Enabled and PeriodTemperatures survive a rebuild because discovery does not produce them, which
	// is why none of the three is counted as a loss.
	public static void Apply(HeatingConfig config, SetupPlan plan)
	{
		ArgumentNullException.ThrowIfNull(config);
		ArgumentNullException.ThrowIfNull(plan);

		HashSet<string> rebuilding = new(plan.Rebuilds.Select(rebuild => rebuild.AreaId), StringComparer.Ordinal);

		// By index, so a room keeps its place and a duplicated area id rebuilds both rows, not just the first.
		for (int index = 0; index < config.Areas.Count; index++)
		{
			AreaConfig existing = config.Areas[index];

			if (existing.AreaId is not { Length: > 0 } areaId || !rebuilding.Contains(areaId))
				continue;

			config.Areas[index] = new AreaConfig
			{
				AreaId = areaId,
				Enabled = existing.Enabled,
				PeriodTemperatures = existing.PeriodTemperatures
			};
		}

		// Grown as it goes, so a registry naming an Area twice cannot slip two rows past it either.
		HashSet<string> present = new(AreaIdsOf(config), StringComparer.Ordinal);

		foreach (AreaConfig added in plan.NewAreas)
			if (added.AreaId is not { Length: > 0 } areaId || present.Add(areaId))
				config.Areas.Add(added);
	}

	private static IEnumerable<string> AreaIdsOf(HeatingConfig config) =>
		config.Areas
			.Select(area => area.AreaId)
			.Where(areaId => areaId is { Length: > 0 })
			.Select(areaId => areaId!);

	// A person with no device tracker is never seeded: one can never resolve to home or away.
	/// <summary>Names the people Home Assistant knows in <c>Global.Persons</c>, while nobody is named yet; the ids written.</summary>
	public static IReadOnlyList<string> SeedPersons(HeatingConfig config, IHaContext ha)
	{
		ArgumentNullException.ThrowIfNull(config);
		ArgumentNullException.ThrowIfNull(ha);

		if (config.Global.Persons.Count > 0)
			return [];

		List<string> persons = [.. ha.GetAllEntities()
			.Select(entity => entity.EntityId)
			.Where(entityId => entityId.HasDomain("person"))
			.Where(entityId => ha.AttrStringList(entityId, "device_trackers").Count > 0)
			.Distinct(StringComparer.Ordinal)
			.Order(StringComparer.Ordinal)];

		if (persons.Count == 0)
			return [];

		config.Global.Persons = persons;
		return persons;
	}

	private static int PinnedEntityCount(AreaConfig area) =>
		(area.Heaters?.Count ?? 0)
		+ (area.TemperatureSensors?.Count ?? 0)
		+ (area.WindowSensor is { Length: > 0 } ? 1 : 0)
		+ (area.PowerSensor is { Length: > 0 } ? 1 : 0)
		+ (area.Neighbour is { Length: > 0 } ? 1 : 0)
		+ (area.ExcludeEntities?.Count ?? 0);

	/// <summary>How many settings a rebuild of this room would drop.</summary>
	public static int OverrideCount(AreaConfig area) =>
		(area.OverrideDurationMinutes is not null ? 1 : 0)
		+ (area.LowestNightTemperature is not null ? 1 : 0)
		+ (area.FrostTemperature is not null || area.FrostProtection is not null ? 1 : 0)
		+ (area.AwayTemperature is not null ? 1 : 0)
		+ (area.BoostTemperature is not null ? 1 : 0);
}
