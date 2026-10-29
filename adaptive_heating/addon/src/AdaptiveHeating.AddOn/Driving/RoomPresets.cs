using Adaptive.Settings;

namespace AdaptiveHeating.AddOn.Driving;

/// <summary>The presets on a room's card, one per house-mode kind, named as the integration's preset list names them.</summary>
internal static class RoomPresets
{
	// core/card.py's PRESETS: the platform's four, then the integration's three. A test holds the two lists together.
	private static readonly Dictionary<ModeKind, string> Names = new()
	{
		[ModeKind.Normal] = "home",
		[ModeKind.Sleep] = "sleep",
		[ModeKind.Away] = "away",
		[ModeKind.Boost] = "boost",
		[ModeKind.PlannedToArrive] = "planned_to_arrive",
		[ModeKind.OnTheWay] = "on_the_way",
		[ModeKind.TemporarilyAway] = "temporarily_away",
	};

	/// <summary>The platform's own word for no preset, which is no choice and no unknown name.</summary>
	public const string None = "none";

	public static IReadOnlyCollection<string> All => Names.Values;

	/// <summary>The preset a room is written for a house-mode kind.</summary>
	// Guest has no preset: the guest hold is the house's gate, and the room runs Home under it.
	public static string For(ModeKind kind) => Names.TryGetValue(kind, out string? name) ? name : Names[ModeKind.Normal];

	/// <summary>The kind a preset stands for, or <c>null</c> where the integration's list holds no such name.</summary>
	public static ModeKind? KindOf(string preset)
	{
		foreach ((ModeKind kind, string name) in Names)
		{
			if (string.Equals(name, preset, StringComparison.Ordinal))
				return kind;
		}

		return null;
	}
}
