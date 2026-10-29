using Microsoft.AspNetCore.Components;

namespace AdaptiveLighting.Web.Presentation;

/// <summary>What kind of value a sentence token holds, which decides how it is written and how it is picked.</summary>
/// <remarks>The contract between the component rendering the token and the page receiving the edit.</remarks>
public enum TokenKind
{
	/// <summary>A span of time. Carried in whole seconds; written as "30 s", "10 min", "2 h".</summary>
	Duration,

	/// <summary>A proportion. Carried as percent 0-100; written as "50 %". The config's 0-1 factors convert.</summary>
	Percentage,

	/// <summary>A bare quantity with a unit: lux, degrees, kelvin. Carried as its own number.</summary>
	Number,

	/// <summary>One of a fixed set, carried as the option's own key.</summary>
	Choice,

	/// <summary>A yes/no, written as the words it means in the sentence and carried as <c>true</c>/<c>false</c>.</summary>
	/// <remarks>Its own kind: a toggle flips where it stands, with no popover.</remarks>
	Toggle,

	/// <summary>A Home Assistant entity id. Carried verbatim.</summary>
	Entity
}

/// <summary>Where a token's value came from. Only <see cref="Own"/> draws the amber dot.</summary>
public enum TokenOrigin
{
	/// <summary>Provenance does not apply. The house's own defaults have nothing to inherit from.</summary>
	None,

	Inherited,

	Own
}

/// <summary>One value a token offers, as it is written and as it is carried, always culture-invariant.</summary>
/// <remarks>
///     <c>Key</c> and <c>Kind</c> let an option answer the sentence's question by writing a different setting,
///     which keeps sentinel values out of the document.
/// </remarks>
public sealed record TokenChoice(string Text, string Value, string? Key = null, TokenKind? Kind = null);

/// <summary>One piece of a sentence: either prose or an inline value.</summary>
public abstract record SentencePart;

/// <summary>Prose. Rendered as written, spaces and punctuation included.</summary>
public sealed record SentenceText(string Text) : SentencePart;

/// <summary>A small drawing set inline in a sentence, where a picture says what the prose cannot.</summary>
/// <remarks><see cref="Sentence.PlainText"/>, a screen reader and a test all read the alt text in the drawing's place, so it is never optional.</remarks>
public sealed record SentenceFigure(string AltText, RenderFragment Content) : SentencePart;

/// <summary>A value inside a sentence, rendered as the inline control that changes it.</summary>
/// <remarks>
///     <c>Key</c> is what the page switches on to apply the edit, written with <c>nameof</c> at both ends.
///     <c>Label</c> is the control's accessible name, since the prose around it reaches no screen reader or toast.
/// </remarks>
public sealed record SentenceToken(
	string Key,
	TokenKind Kind,
	string Text,
	TokenOrigin Origin,
	string Label,
	IReadOnlyList<TokenChoice> Choices,
	string? HouseText = null,
	bool Editable = true) : SentencePart;

/// <summary>One sentence: prose with its values inline, in reading order.</summary>
public sealed record Sentence(IReadOnlyList<SentencePart> Parts)
{
	/// <summary>The sentence as plain prose, values included but unmarked, for titles, logs, toasts and tests.</summary>
	public string PlainText => string.Concat(Parts.Select(part => part switch
	{
		SentenceText text => text.Text,
		SentenceToken token => token.Text,
		SentenceFigure figure => figure.AltText,
		_ => string.Empty
	}));

	/// <summary>How many of this sentence's values are the room's own, which is what the amber dots count.</summary>
	public int OwnValueCount => Parts.Count(part => part is SentenceToken { Origin: TokenOrigin.Own });
}

/// <summary>One token changed, handed to the page to apply.</summary>
/// <remarks>
///     A request, never a write: the sentence view leaves the document alone and the page puts its own copy
///     through the one save pipeline. The value arrives as an invariant string whatever the kind.
/// </remarks>
public sealed record SentenceEdit(string Key, TokenKind Kind, string Value)
{
	public int Seconds => (int)Math.Round(Number, MidpointRounding.AwayFromZero);

	/// <summary>The value as whole minutes. A <see cref="TokenKind.Duration"/> is carried in seconds.</summary>
	public int Minutes => (int)Math.Round(Number / 60.0, MidpointRounding.AwayFromZero);

	public TimeSpan Span => TimeSpan.FromSeconds(Seconds);

	public double Percent => Number;

	/// <summary>The value as a fraction, 0-1, which is what the schema's <c>*Factor</c> properties hold.</summary>
	public double Fraction => Number / 100.0;

	/// <summary>The value as a number, whatever its unit. Zero when the value is not numeric.</summary>
	public double Number =>
		double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : 0;

	public int Integer => (int)Math.Round(Number, MidpointRounding.AwayFromZero);

	public bool Flag => bool.TryParse(Value, out bool parsed) && parsed;

	/// <summary>The value as one of an enum's members.</summary>
	public bool TryEnum<TEnum>(out TEnum value) where TEnum : struct, Enum =>
		Enum.TryParse(Value, ignoreCase: false, out value);
}
