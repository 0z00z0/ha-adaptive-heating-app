using System.Reflection;

namespace AdaptiveLighting.Web.Presentation;

/// <summary>The cache-busting token on every asset URL, shared by both designs.</summary>
/// <remarks>The commit sha after the '+' in the informational version: the version number alone is bumped per
/// release, so two deploys off one preview would share a URL.</remarks>
public static class AssetToken
{
	public static string Text { get; } = Read(typeof(AssetToken).Assembly);

	private static string Read(Assembly assembly)
	{
		string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
			?? assembly.GetName().Version?.ToString()
			?? "0";

		int plus = version.IndexOf('+');
		string token = plus >= 0 ? version[(plus + 1)..] : version;

		return token.Length > 12 ? token[..12] : token;
	}
}
