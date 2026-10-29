namespace AdaptiveLighting.Web.Presentation;

/// <summary>Builds the prefilled "new issue" link the Configuration page's feedback button opens.</summary>
/// <remarks>
///     Nothing is ever submitted from inside the app: this composes a GitHub <c>issues/new</c> URL and nothing
///     else. Every value goes through <see cref="Uri.EscapeDataString"/> so a version string (or anything else
///     added to the body later) cannot introduce a stray <c>&amp;</c> or <c>=</c> and corrupt the query string.
/// </remarks>
public static class FeedbackUrl
{
	private const string IssueBase = "https://github.com/0z00z0/ha-adaptive-heating/issues/new";

	/// <summary>The link for reporting feedback against a specific running version.</summary>
	public static string Build(string version)
	{
		string title = Uri.EscapeDataString("Feedback: ");
		string body = Uri.EscapeDataString($"Version: {version}\n\n");

		return $"{IssueBase}?title={title}&body={body}";
	}
}
