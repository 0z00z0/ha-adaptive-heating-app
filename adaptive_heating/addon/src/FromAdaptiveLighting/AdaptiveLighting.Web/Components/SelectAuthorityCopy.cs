using Microsoft.AspNetCore.Components;

namespace Adaptive.UI;

/// <summary>Every word the selection-authority panel puts on screen, gathered so the panel's own parameters are
/// the state it draws and nothing else.</summary>
/// <remarks>
///     The two notes read the direction in force, so a caller rebuilds this record as the direction changes.
/// </remarks>
public sealed record SelectAuthorityCopy
{
	/// <summary>The picker's label.</summary>
	public string Label { get; init; } = "";

	/// <summary>The label's popover body. Unset leaves the label bare.</summary>
	public RenderFragment? LabelHelp { get; init; }

	/// <summary>The help line under the picker, saying what having no helper at all means.</summary>
	public RenderFragment? Note { get; init; }

	public string NoneLabel { get; init; } = "(none)";

	public string Placeholder { get; init; } = "input_select.helper";

	/// <summary>Why there is nothing to pick, worded by the caller.</summary>
	public string EmptyNote { get; init; } = EntityPicker.DefaultEmptyNote;

	public string AuthorityLabel { get; init; } = "Set by:";

	public RenderFragment? AuthorityHelp { get; init; }

	/// <summary>The help line under the radios, worded for the direction in force.</summary>
	public string AuthorityNote { get; init; } = "";

	/// <summary>What an entity Home Assistant does not know costs, which differs by direction.</summary>
	public string UnavailableNote { get; init; } = "";
}
