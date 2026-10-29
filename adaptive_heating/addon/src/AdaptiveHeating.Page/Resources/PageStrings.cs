namespace AdaptiveHeating.Page.Resources;

/// <summary>
/// Names the resource set every word on the page comes from. Home Assistant reads none of this page's markup
/// and translates none of its text, so the page carries its own words and picks its own language.
/// </summary>
/// <remarks>A second language is a resource file beside <c>PageStrings.resx</c>, named for its culture, with
/// no change to any component. Not static: it is the type argument of the framework's own localiser.</remarks>
public sealed class PageStrings
{
	private PageStrings()
	{
	}
}
