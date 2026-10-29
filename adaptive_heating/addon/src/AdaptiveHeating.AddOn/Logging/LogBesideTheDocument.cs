using AdaptiveLighting.NetDaemon;

namespace AdaptiveHeating.AddOn.Logging;

/// <summary>Where the durable log goes: a folder beside the settings document, in the add-on's own /data.</summary>
internal static class LogBesideTheDocument
{
	/// <summary>The log directory beside a settings document, or <c>null</c> where the path names no directory.</summary>
	public static string? Directory(string? documentPath) =>
		documentPath is { Length: > 0 } && Path.GetDirectoryName(Path.GetFullPath(documentPath)) is { Length: > 0 } directory
			? Path.Combine(directory, DurableLogFile.FolderName)
			: null;

	/// <summary>The document's file stem, so two documents in one directory cannot collide.</summary>
	public static string Stem(string? documentPath, string fallback)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fallback);

		if (documentPath is not { Length: > 0 })
			return fallback;

		string stem = Path.GetFileNameWithoutExtension(documentPath);

		return stem.Length > 0 ? stem : fallback;
	}
}
