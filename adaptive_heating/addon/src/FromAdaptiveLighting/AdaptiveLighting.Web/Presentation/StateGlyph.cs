using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Web.Presentation;

/// <summary>How one room state is drawn: a colour family, a word, and whether it blinks.</summary>
/// <remarks>One record, so a caller cannot draw the colour of one state beside the word of another.</remarks>
public sealed record StateMark(string Family, string Word, bool Blinks);

/// <summary>The mapping from a live room state to the mark that stands for it.</summary>
public static class StateGlyph
{
	public static StateMark For(AreaState state) => state switch
	{
		AreaState.AutoActive => new StateMark("state-machine", "auto", false),
		AreaState.WarmingUp => new StateMark("state-machine", "warming up", false),

		// The human states share one family: a person decided, and the word says which.
		AreaState.OverriddenOn => new StateMark("state-human", "set by hand", false),
		AreaState.SceneHold => new StateMark("state-human", "held", false),

		AreaState.NotReporting => new StateMark("state-warn", "not reporting", false),
		AreaState.Disabled => new StateMark("state-idle", "switched off", false),
		AreaState.Away => new StateMark("state-idle", "house away", false),
		_ => new StateMark("state-idle", "unknown", false)
	};

	public static StateMark For(AreaSnapshot snapshot) => For(snapshot.State);
}
