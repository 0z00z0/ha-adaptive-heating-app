using Adaptive.Settings;

namespace Adaptive.Engine;

/// <summary>Finds the Home Assistant dropdown that is obviously the house mode, and says what each option means.</summary>
// Adoption writes kinds and nothing else: no scene, no reset trigger, no period switching to it, so an adopted
// select is read and never written. Adopting the wrong dropdown holds a whole house Away, which is why the bar
// is two distinct kinds, one of them Normal, and only one qualifying select in the house.
internal static class HouseModeAutoDetect
{
	private const string SelectDomain = "input_select";
	private const string OptionsAttribute = "options";

	// Matched as a substring of the lower-cased option text. An unrecognised word classifies as Normal.
	private static readonly (ModeKind Kind, string[] Words)[] Vocabulary =
	[
		(ModeKind.Sleep, ["sleep", "sleeping", "asleep", "night", "sover", "sove", "natt", "senga"]),
		(ModeKind.Away,  ["away", "gone", "out", "empty", "borte", "ute", "reist"]),
		(ModeKind.Guest, ["guest", "guests", "visitor", "visitors", "gjest", "gjester", "besok", "besøk"]),
		(ModeKind.Normal, ["home", "normal", "default", "day", "hjemme", "dag", "vanlig"]),
	];

	/// <summary>The house-mode configuration this instance obviously wants, or <c>null</c> when it is not obvious.</summary>
	public static HouseModeConfig? Detect(IHaContext ha, ILogger logger)
	{
		ArgumentNullException.ThrowIfNull(ha);
		ArgumentNullException.ThrowIfNull(logger);

		List<HouseModeConfig> candidates = [];

		foreach (string entityId in SelectIds(ha))
		{
			IReadOnlyList<string> options = ha.GetState(entityId).AttrStringList(OptionsAttribute);
			if (options.Count == 0)
				continue;

			List<HouseModeOptionConfig> classified =
				[.. options
					.Where(option => !string.IsNullOrWhiteSpace(option))
					.Select(option => new HouseModeOptionConfig { Value = option.Trim(), Kind = KindOf(option) })];

			// "Eco / Comfort / Boost" classifies as three Normals and is ignored.
			bool hasNormal = classified.Any(option => option.Kind == ModeKind.Normal);
			int distinctKinds = classified.Select(option => option.Kind).Distinct().Count();

			if (hasNormal && distinctKinds >= 2)
				candidates.Add(new HouseModeConfig { Entity = entityId, Options = classified });
		}

		switch (candidates.Count)
		{
			case 0:
				return null;

			case 1:
				HouseModeConfig detected = candidates[0];
				logger.LogInformation(
					"Adopted {Entity} as the house mode ({Options}). Nothing switches it automatically; add "
					+ "a reset trigger or a period that sets it on the Configuration page.",
					detected.Entity,
					string.Join(", ", detected.Options.Select(option => $"{option.Value}={option.Kind}")));
				return detected;

			default:
				logger.LogInformation(
					"{Count} dropdowns could be the house mode ({Entities}); leaving it unset rather than choosing. Pick one on the Configuration page.",
					candidates.Count, string.Join(", ", candidates.Select(candidate => candidate.Entity)));
				return null;
		}
	}

	private static IEnumerable<string> SelectIds(IHaContext ha)
	{
		try
		{
			return [.. ha.GetAllEntities()
				.Select(entity => entity.EntityId)
				.Where(entityId => entityId.HasDomain(SelectDomain))
				.Distinct(StringComparer.Ordinal)];
		}
		catch (InvalidOperationException)
		{
			// NetDaemon's state cache throws until its first connection completes.
			return [];
		}
	}

	private static ModeKind KindOf(string option)
	{
		string text = option.Trim().ToLowerInvariant();

		foreach ((ModeKind kind, string[] words) in Vocabulary)
			if (words.Any(word => text.Contains(word, StringComparison.Ordinal)))
				return kind;

		return ModeKind.Normal;
	}
}
