using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Web.Presentation;

/// <summary>A room's behaviour, projected into the handful of sentences the overview shows.</summary>
/// <remarks>
///     Every token's key is the <see cref="AreaSettings"/> property it changes, so the page applying an edit
///     switches on <c>nameof</c> and the compiler keeps the two in step.
/// </remarks>
public static class AreaSentences
{
	/// <summary>Joins flag phrases the way English does, with a comma before the final "and".</summary>
	/// <param name="phrases">The phrases, already worded as verb clauses.</param>
	public static string JoinClauses(IReadOnlyList<string> phrases)
	{
		ArgumentNullException.ThrowIfNull(phrases);

		return phrases.Count switch
		{
			0 => string.Empty,
			1 => phrases[0],
			2 => $"{phrases[0]}, and {phrases[1]}",
			_ => $"{string.Join(", ", phrases.Take(phrases.Count - 1))}, and {phrases[^1]}"
		};
	}
}
