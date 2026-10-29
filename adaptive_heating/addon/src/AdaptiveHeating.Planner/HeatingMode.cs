namespace AdaptiveHeating.Planner;

/// <summary>
/// The four modes, each holding one temperature per room. A mode is a name; what it is worth in a room is the
/// number typed against it here.
/// </summary>
/// <remarks>The planner resolves a mode to a temperature before it calls the integration, so the integration
/// never learns what a mode is.</remarks>
public enum HeatingMode
{
	/// <summary>The frost protection. Nothing anywhere falls below it.</summary>
	Away,

	Night,

	Home,

	/// <summary>What the house mode's boost kind runs.</summary>
	Boost
}

/// <summary>The four modes, in one fixed order.</summary>
public static class HeatingModes
{
	public static IReadOnlyList<HeatingMode> All { get; } =
		[HeatingMode.Away, HeatingMode.Night, HeatingMode.Home, HeatingMode.Boost];
}
