using System.Reflection;

namespace AdaptiveHeating.Page.Presentation;

/// <summary>The version each half runs, which is what explains a setting somebody cannot find.</summary>
/// <param name="AddOn">The add-on's own version.</param>
/// <param name="Integration">What the integration reported, or <c>null</c> while nothing has been heard from it.</param>
/// <param name="Vocabulary">The vocabulary version this half states.</param>
public sealed record HalfVersions(string AddOn, string? Integration, int Vocabulary)
{
	/// <summary>The add-on's own version, taken from the assembly rather than written down twice.</summary>
	public static string AddOnVersion { get; } = Read(typeof(HalfVersions).Assembly);

	private static string Read(Assembly assembly)
	{
		string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
			?? assembly.GetName().Version?.ToString()
			?? "unknown";

		int plus = version.IndexOf('+', StringComparison.Ordinal);
		return plus >= 0 ? version[..plus] : version;
	}
}
