using AdaptiveLighting.Configuration;

namespace Adaptive.Engine;

/// <summary>The word a house mode is published and logged under.</summary>
// ModeKind.Normal goes out as "Home". Home Assistant automations match on this text, so nothing else may
// produce or read a mode word.
public static class HouseModeName
{
	/// <summary>What the everyday kind is called outside the engine.</summary>
	public const string Home = "Home";

	/// <summary>The word <paramref name="kind"/> is published under.</summary>
	public static string Of(ModeKind kind) =>
		kind == ModeKind.Normal ? Home : kind.ToString();

	/// <summary>The kind <paramref name="name"/> stands for, or <see cref="ModeKind.Normal"/> for anything else.</summary>
	public static ModeKind Parse(string? name) =>
		Enum.TryParse(name, out ModeKind kind) ? kind : ModeKind.Normal;

	/// <summary>A sleep option's name as a mode: <c>Night mode</c>, the name alone when it already ends in "mode", <c>sleep mode</c> when it has none.</summary>
	public static string SleepPhrase(string? optionName) =>
		string.IsNullOrWhiteSpace(optionName) ? "sleep mode"
		: optionName.EndsWith("mode", StringComparison.OrdinalIgnoreCase) ? optionName
		: optionName + " mode";
}

/// <summary>Which of the engine's own activation rules put the house on its mode, as opposed to a person choosing it.</summary>
public enum ModeForceSource
{
	/// <summary>An <see cref="HouseModeOptionConfig.ActivateWhileOn"/> entity is on, so this option wins over whatever the select reads.</summary>
	// Nothing writes the select, so this mode change has no visible cause.
	WhileEntityOn = 0,

	/// <summary>The house went <see cref="HouseModeOptionConfig.ActivateAfterNoMotionMinutes"/> without motion and the engine wrote the select.</summary>
	// Not known across a restart.
	NoMotionTimeout = 1
}

/// <summary>The house mode the engine put itself on, and what is holding it there.</summary>
// A null ForcedMode means the select's value is the whole story. Every kind can be forced, not only Away.
// EntityId is set for WhileEntityOn and null for a no-motion activation.
public sealed record ForcedMode(
	ModeKind Kind,
	string OptionValue,
	ModeForceSource Source,
	string? EntityId = null,
	string? EntityState = null)
{
	/// <summary>One sentence naming what put the house on this mode, shared by the log and the UI.</summary>
	public string Describe() =>
		Source is ModeForceSource.WhileEntityOn && EntityId is { Length: > 0 }
			? $"{Kind} mode is forced while {EntityId} is {EntityState ?? "on"}."
			: $"{OptionValue} was set after the configured time without movement.";
}

/// <summary>An immutable snapshot of everything house-wide an area needs.</summary>
// The orchestrator owns the stream of these; areas only read them. ActiveKind is the kind of the option the
// select stands on, Normal when unconfigured, and is the whole answer on which mode the house is in.
// KillSwitchActive forbids the engine from commanding anything. IsAnyoneHome is published so a person can see
// the trackers working and is never composed in, so a phone left on a worktop cannot hold a house away.
public sealed record HouseState(
	bool IsAnyoneHome,
	ModeKind ActiveKind,
	bool KillSwitchActive)
{
	/// <summary>The raw house-mode option string, or <c>null</c> when unconfigured, unknown or unavailable.</summary>
	public string? ModeValue { get; init; }

	/// <summary>The active option's <c>scene.*</c> when it names one, whatever its kind.</summary>
	public string? ActiveScene { get; init; }

	/// <summary>The effective option's own name as written in the document, or <c>null</c> when nothing classifies.</summary>
	// Display only, never a key.
	public string? ActiveModeName { get; init; }

	/// <summary>What is forcing <see cref="ActiveKind"/>, or <c>null</c> when the select's own value is the answer.</summary>
	// Part of the record equality, so the orchestrator republishes when a forcing entity flips even though
	// ActiveKind and ModeValue have not moved.
	public ForcedMode? Forced { get; init; }

	/// <summary>The state the engine starts in, before presence and mode have reported.</summary>
	public static readonly HouseState Initial = new(true, ModeKind.Normal, false);
}
