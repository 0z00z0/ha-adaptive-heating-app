using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Web.Presentation;

/// <summary>The identity sheet's one sentence, split where the markup has to put something interactive in.</summary>
public sealed record IdentityParts(string BeforeName, string AfterName, string BeforeDelay, string AfterDelay);

/// <summary>The two facts discovery cannot know: what this house is called and who lives in it.</summary>
public static class IdentitySentence
{
	// The same string CreateDefault seeds; a second spelling renames every house that skipped the sheet.
	public const string DefaultHouseName = "Adaptive Heating";

	/// <summary>The prose around the sheet's three controls.</summary>
	public static IdentityParts Parts { get; } = new(
		"This house is called ",
		". ",
		" — shown as home or away, never acted on; the house reads as empty ",
		" after the last person leaves.");

	public static string Display(string? configName) =>
		string.IsNullOrWhiteSpace(configName) ? DefaultHouseName : configName.Trim();

	// null, never DefaultHouseName: writing the placeholder out as a real name makes a house that never answered
	// indistinguishable from one that chose that name.
	public static string? Normalize(string? typed) =>
		string.IsNullOrWhiteSpace(typed) ? null : typed.Trim();

	/// <summary>The empty-house delay as the sentence's one real token, sharing the House tab's choices and label.</summary>
	public static SentenceToken Delay(int minutes) => new(
		nameof(GlobalConfig.AwayDebounceMinutes),
		TokenKind.Duration,
		TokenFormat.DurationFromMinutes(Math.Max(0, minutes)),
		TokenOrigin.None,
		"Count the house as empty after",
		HouseSentences.AwayDebounceChoices);

	public static string PlainText(string? houseName, IReadOnlyList<string> people, int minutes)
	{
		ArgumentNullException.ThrowIfNull(people);

		return string.Concat(
			Parts.BeforeName,
			Display(houseName),
			Parts.AfterName,
			PeopleClause(people),
			Parts.BeforeDelay,
			TokenFormat.DurationFromMinutes(Math.Max(0, minutes)),
			Parts.AfterDelay);
	}

	/// <summary>The checklist's status line: the answers as they stand, never a tick for having visited.</summary>
	public static string StatusLine(string? houseName, IReadOnlyList<string> people, int minutes)
	{
		ArgumentNullException.ThrowIfNull(people);

		string who = people.Count switch
		{
			0 => "nobody counted",
			<= 3 => string.Join(", ", people),
			_ => $"{string.Join(", ", people.Take(2))} and {people.Count - 2} more"
		};

		return $"{Display(houseName)} · {who} · empty after {TokenFormat.DurationFromMinutes(Math.Max(0, minutes))}";
	}

	private static string PeopleClause(IReadOnlyList<string> people) => people.Count switch
	{
		0 => "Nobody",
		1 => people[0],
		2 => $"{people[0]} and {people[1]}",
		_ => $"{string.Join(", ", people.Take(people.Count - 1))} and {people[^1]}"
	};
}
