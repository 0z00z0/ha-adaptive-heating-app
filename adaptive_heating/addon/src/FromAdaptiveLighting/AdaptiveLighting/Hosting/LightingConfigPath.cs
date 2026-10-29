using Microsoft.Extensions.Configuration;

namespace Adaptive.Settings;

/// <summary>Where a resolved configuration document came from, so the UI can say which file it is editing.</summary>
public enum ConfigLocationSource
{
	External,

	/// <summary>The external file, created on this run, holding a fresh
	///     <see cref="HeatingConfig.CreateDefault"/> and never a copy of the in-tree example.</summary>
	SeededFromTree,

	InTreeFallback
}

/// <summary>The resolved location of this host's configuration document.</summary>
public sealed record ConfigLocation(string Path, ConfigLocationSource Source, string? Warning)
{
	public bool SurvivesDeploy => Source is not ConfigLocationSource.InTreeFallback;
}

/// <summary>Works out which file is the heating configuration document for this host, and seeds it on first run.</summary>
/// <remarks>
///     The editable document lives outside the image, which an add-on update replaces whole. Resolved once at
///     start-up and baked into the singleton <see cref="HeatingConfigStore"/>, so no request, component or browser
///     can influence it, which holds the UI's write surface to one file. The only type in the engine or the UI that
///     touches <see cref="IConfiguration"/>; the root configuration object carries the Supervisor's token and never
///     leaves here.
/// </remarks>
public static class HeatingConfigPath
{
	/// <summary>Where the editable document lives, answered in the add-on's tracked <c>appsettings.json</c>.</summary>
	public const string ConfigPathKey = "AdaptiveHeating:DocumentPath";

	private const string DocumentName = "AdaptiveHeating.yaml";

	/// <summary>Resolves the document's absolute path, writing a starting document to it on first run.</summary>
	/// <returns>The chosen location. The file need not exist; a host whose document is missing must still start.</returns>
	public static ConfigLocation Resolve(IConfiguration configuration, string contentRootPath, ILogger logger)
	{
		ArgumentNullException.ThrowIfNull(configuration);
		ArgumentNullException.ThrowIfNull(logger);
		ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

		string inTree = Path.Combine(Path.GetFullPath(contentRootPath), DocumentName);

		if (configuration[ConfigPathKey] is not { Length: > 0 } configured)
		{
			logger.LogWarning(
				"{Key} is not set, so the heating configuration falls back to the in-tree file at {Path}. "
				+ "Edits there are destroyed by the next add-on update — set {Key} to a path outside the image.",
				ConfigPathKey, inTree, ConfigPathKey);

			return new ConfigLocation(
				inTree,
				ConfigLocationSource.InTreeFallback,
				$"{ConfigPathKey} is not set in appsettings.json, so this host is editing the file inside its own deploy folder. The next update will overwrite it.");
		}

		string external = Path.GetFullPath(configured, contentRootPath);

		if (File.Exists(external))
			return new ConfigLocation(external, ConfigLocationSource.External, null);

		string? directory = Path.GetDirectoryName(external);

		if (directory is null)
			return Fallback(inTree, external, "it has no directory", logger);

		// The parent is the test, not the directory itself: /data exists on a Home Assistant box while
		// /data/adaptive-heating does not. With the parent missing too, the path belongs to another machine.
		if (!Directory.Exists(directory) && !Directory.Exists(Path.GetDirectoryName(directory) ?? directory))
			return Fallback(inTree, external, "neither it nor its parent directory exists on this machine", logger);

		try
		{
			Directory.CreateDirectory(directory);

			File.WriteAllText(external, HeatingConfigDocument.Serialize(HeatingConfig.CreateDefault()));

			logger.LogInformation(
				"Created a starting heating configuration at {External}: no entity named, no room and no period. "
				+ "Rooms are discovered from the Home Assistant areas whose heaters carry the heating label, and "
				+ "everything is editable on the settings page.",
				external);

			return new ConfigLocation(external, ConfigLocationSource.SeededFromTree, null);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return Fallback(inTree, external, $"it could not be created ({exception.Message})", logger);
		}
	}

	private static ConfigLocation Fallback(string inTree, string external, string why, ILogger logger)
	{
		logger.LogWarning(
			"The configured heating configuration path {External} is unusable: {Why}. Falling back to the in-tree "
			+ "file at {InTree}. This is expected on a development machine; on a Home Assistant host it means edits "
			+ "will not survive an update.",
			external, why, inTree);

		return new ConfigLocation(
			inTree,
			ConfigLocationSource.InTreeFallback,
			$"{ConfigPathKey} points at '{external}', but {why}. This host is editing the file inside its own deploy folder instead, and the next update will overwrite it. That is normal when running locally.");
	}
}
