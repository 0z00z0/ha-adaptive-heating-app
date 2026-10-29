namespace AdaptiveLighting.Web.Presentation;

/// <summary>
///     Something a page has to say for a moment: the text, and when it stops being said.
/// </summary>
/// <remarks>
///     Read at render time against the page's own clock, so nothing has to be cleared by a timer. The message
///     carries words and an expiry and never a class name, so each design paints it its own way.
/// </remarks>
/// <param name="Text">What the line says. Empty when there is nothing to say.</param>
/// <param name="Until">When the line stops being shown, or <c>null</c> when it stands until something replaces it.</param>
public readonly record struct TransientMessage(string Text, DateTimeOffset? Until)
{
	/// <summary>Nothing to say.</summary>
	public static TransientMessage None => new(string.Empty, null);

	/// <summary>A line that clears itself after <paramref name="lingers"/>.</summary>
	public static TransientMessage For(string text, DateTimeOffset from, TimeSpan lingers) =>
		new(text, from + lingers);

	/// <summary>A line that stands until something replaces it.</summary>
	public static TransientMessage Standing(string text) => new(text, null);

	/// <summary>Whether the line is still being said at this moment.</summary>
	public bool IsShownAt(DateTimeOffset now) =>
		Text.Length > 0 && (Until is not { } until || now < until);

	/// <summary>The line at this moment, or nothing once it has expired.</summary>
	public string TextAt(DateTimeOffset now) => IsShownAt(now) ? Text : string.Empty;
}
