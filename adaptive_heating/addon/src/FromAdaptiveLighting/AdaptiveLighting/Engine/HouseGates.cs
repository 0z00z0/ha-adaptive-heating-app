using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Engine;

/// <summary>The house-wide and room-wide gates, in the order they are asked.</summary>
// The order is the ladder itself: moving a member changes which gate a refusal names.
internal enum HouseGate
{
	None = 0,

	KillSwitch,

	Disabled,

	/// <summary>A guest mode is holding the room.</summary>
	SceneHold,

	/// <summary>An entity the room watches is on.</summary>
	EntityOn
}

/// <summary>What the gates are judged against at one instant.</summary>
// BlockingEntity is a function because the callers that stop before that gate must not read those entities.
internal readonly record struct HouseGateState(
	bool KillSwitchActive,
	bool Enabled,
	ModeKind Mode,
	Func<string?> BlockingEntity);

/// <summary>The one ordered gate evaluation the engine reads.</summary>
internal static class HouseGates
{
	/// <summary>The first closed gate between <paramref name="from"/> and <paramref name="until"/>, or <see cref="HouseGate.None"/>.</summary>
	public static HouseGate FirstClosed(in HouseGateState state, HouseGate from, HouseGate until) =>
		FirstClosed(state, from, until, out _);

	/// <summary>As above; <paramref name="blockingEntity"/> names the entity that closed <see cref="HouseGate.EntityOn"/>.</summary>
	public static HouseGate FirstClosed(in HouseGateState state, HouseGate from, HouseGate until, out string? blockingEntity)
	{
		blockingEntity = null;

		if (Asked(HouseGate.KillSwitch) && state.KillSwitchActive)
			return HouseGate.KillSwitch;

		if (Asked(HouseGate.Disabled) && !state.Enabled)
			return HouseGate.Disabled;

		if (Asked(HouseGate.SceneHold) && state.Mode == ModeKind.Guest)
			return HouseGate.SceneHold;

		if (Asked(HouseGate.EntityOn) && state.BlockingEntity() is { } blocking)
		{
			blockingEntity = blocking;
			return HouseGate.EntityOn;
		}

		return HouseGate.None;

		bool Asked(HouseGate gate) => gate >= from && gate <= until;
	}
}
