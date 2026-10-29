using Adaptive.Settings;

namespace Adaptive.Engine;

/// <summary>Proposes a starting set of rooms from the Areas the registry says hold a heater.</summary>
// Only AreaId and Enabled are written; everything else resolves at run time, so a proposal survives a rename.
internal static class AreaAutoDiscovery
{
	public static IReadOnlyList<AreaConfig> Propose(IAreaRegistry registry, AreaEntityResolver resolver)
	{
		ArgumentNullException.ThrowIfNull(registry);
		ArgumentNullException.ThrowIfNull(resolver);

		List<AreaConfig> proposed = [];

		foreach (string areaId in registry.AreaIds)
		{
			if (string.IsNullOrWhiteSpace(areaId))
				continue;

			AreaDiscovery found = resolver.DiscoverArea(areaId);

			// Heaters alone qualify; a room with no sensor borrows a neighbour's reading or takes on and off.
			// Explicit false, never a flipped Defaults.Enabled: that would switch off every room in a document
			// that never wrote the value.
			if (found.Heaters.Count > 0)
				proposed.Add(new AreaConfig { AreaId = areaId, Enabled = false });
		}

		return proposed;
	}

	// Room roles guessed from the area id, which is a slug of the name people actually use.
	private static readonly string[] Bedroom = ["sov", "soverom", "bedroom", "seng", "sengerom"];
	private static readonly string[] Bathroom = ["bad", "wc", "toalett", "bathroom", "dusj"];
	private static readonly string[] Entrance = ["gang", "inngang", "entre", "hall", "hallway", "entrance", "korridor", "trapp"];
	private static readonly string[] Outdoor = ["ute", "uteplass", "terrasse", "veranda", "hage", "garasje", "garage", "outdoor", "garden", "porch", "carport"];

	/// <summary>What the area id says the room is for, or <c>null</c> where it says nothing.</summary>
	// Named and never written: a guessed temperature would be a number in the document nobody chose.
	internal static string? RoleOf(string? areaId)
	{
		if (areaId is not { Length: > 0 })
			return null;

		List<string> roles = [];

		if (Matches(areaId, Bedroom))
			roles.Add("bedroom");
		else if (Matches(areaId, Bathroom))
			roles.Add("bathroom");

		if (Matches(areaId, Entrance))
			roles.Add("entrance");

		if (Matches(areaId, Outdoor))
			roles.Add("outdoor");

		return roles.Count > 0 ? string.Join("+", roles) : null;
	}

	// Per underscore-separated segment, not substring: inngang_ute matches "ute", utleieleilighet does not. Tokens
	// of four or more also match the end of a compound segment, which is what catches kjellergang.
	private static bool Matches(string areaId, string[] tokens)
	{
		string[] segments = areaId.ToLowerInvariant().Split('_', StringSplitOptions.RemoveEmptyEntries);

		return segments.Any(segment => tokens.Any(token =>
			string.Equals(segment, token, StringComparison.Ordinal)
			|| (token.Length >= 4 && segment.EndsWith(token, StringComparison.Ordinal))));
	}
}
