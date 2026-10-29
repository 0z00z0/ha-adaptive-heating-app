namespace Adaptive.UI;

/// <summary>One choice in a preset dropdown: the number the config stores, and the words a person picks it by.</summary>
public sealed record PresetChoice(double Value, string Label);
