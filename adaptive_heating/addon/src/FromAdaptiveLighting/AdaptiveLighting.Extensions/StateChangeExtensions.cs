using NetDaemon.HassModel.Entities;

namespace Adaptive.Engine;

/// <summary>Questions about a non-generic <see cref="StateChange"/>, the shape the engine subscribes in.</summary>
/// <remarks>Null-tolerant: a change with no <c>New</c> answers false or null, never throws.</remarks>
public static class StateChangeExtensions
{
	/// <summary>Whether the new state reads on.</summary>
	public static bool TurnedOn(this StateChange change) => change.New?.IsOn() ?? false;

	/// <summary>Whether the new state reads off.</summary>
	public static bool TurnedOff(this StateChange change) => change.New?.IsOff() ?? false;

	/// <summary>The entity id the change is about, preferring the new state's and falling back to the old.</summary>
	public static string? EntityId(this StateChange change) => change.New?.EntityId ?? change.Entity?.EntityId;

	/// <summary>Whether the new state equals <paramref name="value"/>, ordinal-ignore-case.</summary>
	public static bool StateBecame(this StateChange change, string value) => change.New.StateIs(value);
}
