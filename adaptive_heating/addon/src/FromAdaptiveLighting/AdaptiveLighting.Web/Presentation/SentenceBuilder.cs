using Microsoft.AspNetCore.Components;

namespace AdaptiveLighting.Web.Presentation;

/// <summary>Builds one sentence from prose and typed values.</summary>
/// <remarks>
///     Each typed method formats its own value, so the words in the sentence and the value carried back cannot
///     disagree. A token reading "10 min" that handed back 10 would set a ten-second timeout and look right.
///     Every <c>key</c> is the property name the page switches on when applying the edit.
/// </remarks>
public sealed class SentenceBuilder
{
	private static readonly IReadOnlyList<TokenChoice> NoChoices = [];

	private readonly List<SentencePart> _parts = [];

	public static SentenceBuilder Start(string text = "")
	{
		SentenceBuilder builder = new();

		return text.Length > 0 ? builder.Text(text) : builder;
	}

	/// <summary>Adds prose, written as given, spaces and punctuation included.</summary>
	public SentenceBuilder Text(string text)
	{
		_parts.Add(new SentenceText(text));

		return this;
	}

	public SentenceBuilder Token(SentenceToken token)
	{
		ArgumentNullException.ThrowIfNull(token);

		_parts.Add(token);

		return this;
	}

	/// <summary>Adds a span of time, written in the largest exact unit and carried in seconds.</summary>
	public SentenceBuilder Duration(
		string key,
		string label,
		int seconds,
		IReadOnlyList<TokenChoice>? choices = null,
		TokenOrigin origin = TokenOrigin.None,
		int? houseSeconds = null,
		bool editable = true) =>
		Token(new SentenceToken(
			key,
			TokenKind.Duration,
			TokenFormat.Duration(seconds),
			origin,
			label,
			choices ?? NoChoices,
			HouseText(origin, houseSeconds is { } house ? TokenFormat.Duration(house) : null),
			editable));

	/// <summary>Adds a proportion, written with a percent sign and carried as 0-100.</summary>
	public SentenceBuilder Percent(
		string key,
		string label,
		double percent,
		IReadOnlyList<TokenChoice>? choices = null,
		TokenOrigin origin = TokenOrigin.None,
		double? housePercent = null,
		bool editable = true) =>
		Token(new SentenceToken(
			key,
			TokenKind.Percentage,
			TokenFormat.Percent(percent),
			origin,
			label,
			choices ?? NoChoices,
			HouseText(origin, housePercent is { } house ? TokenFormat.Percent(house) : null),
			editable));

	/// <summary>Adds a quantity with a unit: lux, degrees, kelvin.</summary>
	public SentenceBuilder Number(
		string key,
		string label,
		double value,
		string unit = "",
		IReadOnlyList<TokenChoice>? choices = null,
		TokenOrigin origin = TokenOrigin.None,
		double? houseValue = null,
		bool editable = true) =>
		Token(new SentenceToken(
			key,
			TokenKind.Number,
			TokenFormat.Number(value, unit),
			origin,
			label,
			choices ?? NoChoices,
			HouseText(origin, houseValue is { } house ? TokenFormat.Number(house, unit) : null),
			editable));

	/// <summary>Adds one of a fixed set of named options; a value no option matches is written as itself.</summary>
	public SentenceBuilder Choice(
		string key,
		string label,
		string value,
		IReadOnlyList<TokenChoice> choices,
		TokenOrigin origin = TokenOrigin.None,
		string? houseValue = null,
		bool editable = true)
	{
		ArgumentNullException.ThrowIfNull(choices);

		return Token(new SentenceToken(
			key,
			TokenKind.Choice,
			WordsFor(choices, value),
			origin,
			label,
			choices,
			HouseText(origin, houseValue is null ? null : WordsFor(choices, houseValue)),
			editable));
	}

	/// <summary>Adds a yes/no, written as what it means here and flipped in place.</summary>
	public SentenceBuilder Toggle(
		string key,
		string label,
		bool value,
		string onText,
		string offText,
		TokenOrigin origin = TokenOrigin.None,
		bool? houseValue = null,
		bool editable = true)
	{
		IReadOnlyList<TokenChoice> choices = TokenChoices.Of((onText, "true"), (offText, "false"));

		return Token(new SentenceToken(
			key,
			TokenKind.Toggle,
			value ? onText : offText,
			origin,
			label,
			choices,
			HouseText(origin, houseValue is { } house ? (house ? onText : offText) : null),
			editable));
	}

	/// <summary>Adds a small drawing inline, for a value that is a shape and not a quantity.</summary>
	public SentenceBuilder Figure(string altText, RenderFragment content)
	{
		ArgumentNullException.ThrowIfNull(content);

		_parts.Add(new SentenceFigure(altText, content));

		return this;
	}

	/// <summary>Adds a clause only when it applies, which is how a setting gated by another one disappears.</summary>
	public SentenceBuilder When(bool condition, Action<SentenceBuilder> clause)
	{
		ArgumentNullException.ThrowIfNull(clause);

		if (condition)
			clause(this);

		return this;
	}

	/// <summary>Adds a Home Assistant entity id, written and carried verbatim.</summary>
	public SentenceBuilder Entity(
		string key,
		string label,
		string entityId,
		IReadOnlyList<TokenChoice>? choices = null,
		TokenOrigin origin = TokenOrigin.None,
		string? houseEntityId = null,
		bool editable = true) =>
		Token(new SentenceToken(
			key,
			TokenKind.Entity,
			entityId,
			origin,
			label,
			choices ?? NoChoices,
			HouseText(origin, houseEntityId),
			editable));

	public Sentence Build() => new([.. _parts]);

	// Carried only when the room has departed from the house: a token already on the house value has no road back,
	// and every caller passes its house default regardless.
	private static string? HouseText(TokenOrigin origin, string? houseText) =>
		origin == TokenOrigin.Own ? houseText : null;

	private static string WordsFor(IReadOnlyList<TokenChoice> choices, string value) =>
		choices.FirstOrDefault(choice => string.Equals(choice.Value, value, StringComparison.Ordinal))?.Text ?? value;
}
