using System.Globalization;
using System.Reflection;

namespace AdaptiveHeating.Page.Presentation;

/// <summary>
/// The languages the page holds words for. English ships; another arrives as a resource file beside it, which
/// builds into a satellite assembly and needs no change to any code.
/// </summary>
public static class PageLanguages
{
	/// <summary>What the page falls back to when the browser asks for nothing it holds.</summary>
	public static CultureInfo Fallback { get; } = CultureInfo.GetCultureInfo("en-GB");

	/// <summary>English, plus every culture a satellite assembly beside this one names.</summary>
	/// <remarks>Read off disk rather than written down, so adding a language is adding a file.</remarks>
	public static IReadOnlyList<CultureInfo> Available()
	{
		List<CultureInfo> found = [Fallback];
		Assembly page = typeof(PageLanguages).Assembly;
		string? beside = Path.GetDirectoryName(page.Location);

		if (string.IsNullOrEmpty(beside))
			return found;

		string satellite = page.GetName().Name + ".resources.dll";

		foreach (string folder in SafeDirectories(beside))
		{
			if (!File.Exists(Path.Combine(folder, satellite)))
				continue;

			if (TryCulture(Path.GetFileName(folder), out CultureInfo culture))
				found.Add(culture);
		}

		return found;
	}

	private static IEnumerable<string> SafeDirectories(string root)
	{
		try
		{
			return Directory.EnumerateDirectories(root);
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			// A language nobody can read is one language missing, never a page that will not start.
			return [];
		}
	}

	private static bool TryCulture(string name, out CultureInfo culture)
	{
		try
		{
			culture = CultureInfo.GetCultureInfo(name);

			return true;
		}
		catch (CultureNotFoundException)
		{
			culture = Fallback;

			return false;
		}
	}
}
