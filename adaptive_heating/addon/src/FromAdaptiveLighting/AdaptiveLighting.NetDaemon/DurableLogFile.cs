using Serilog;

namespace AdaptiveLighting.NetDaemon;

/// <summary>The durable copy of the log: one file a day, pruned by age and by count, in a directory a deploy keeps.</summary>
/// <remarks>
///     Two restarts overwrite Supervisor's buffer, so the lines are also kept beside the configuration document.
///     <see cref="RetainedFileTime"/> is what a reader gets; <see cref="RetainedFileCount"/> times <see cref="MaxFileBytes"/>
///     caps the disk. A fortnight at the measured rate is more bytes than a byte budget alone would keep.
/// </remarks>
internal static class DurableLogFile
{
	/// <summary>The subdirectory the log lives in, beside the document instead of on top of it.</summary>
	// Lighting's command log value; heating has no command log yet.
	public const string FolderName = "log";

	/// <summary>What one day's file may reach before the day rolls early.</summary>
	public const long MaxFileBytes = 4L * 1024 * 1024;

	/// <summary>How many files are kept, so the directory has a ceiling however noisy the house is.</summary>
	public const int RetainedFileCount = 15;

	private const string Extension = ".log";

	// Serilog inserts the date between the stem and the extension, so the separator has to be part of the stem.
	private const string DatePrefix = "-";

	/// <summary>How far back a reader can look in the ordinary case, which is what a byte budget alone cannot promise.</summary>
	public static readonly TimeSpan RetainedFileTime = TimeSpan.FromDays(14);

	/// <summary>The path Serilog rolls, which is never itself written: a date goes in before the extension.</summary>
	public static string PathTemplate(string directory, string stem)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);
		ArgumentException.ThrowIfNullOrWhiteSpace(stem);

		return Path.Combine(directory, stem + DatePrefix + Extension);
	}

	/// <summary>Attaches the durable copy to a logger being built.</summary>
	/// <param name="logger">The configuration the console sink is already on; a second <c>UseSerilog</c> would replace it.</param>
	/// <param name="directory">Where the files go, normally <see cref="FolderName"/> beside the document.</param>
	/// <param name="stem">The document's stem, so two houses sharing a directory cannot collide.</param>
	/// <param name="maxFileBytes">Overrides <see cref="MaxFileBytes"/>, for tests.</param>
	/// <param name="retainedFileCount">Overrides <see cref="RetainedFileCount"/>, for tests.</param>
	public static LoggerConfiguration AddTo(
		LoggerConfiguration logger,
		string directory,
		string stem,
		long maxFileBytes = MaxFileBytes,
		int retainedFileCount = RetainedFileCount)
	{
		ArgumentNullException.ThrowIfNull(logger);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFileBytes);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retainedFileCount);

		string template = PathTemplate(directory, stem);

		RemoveSuperseded(directory, stem);

		// Rolling daily is what makes retention answer in days; rolling on size as well is what keeps the ceiling
		// true on a day that misbehaves. Dropping either leaves one of the two bounds unenforced.
		return logger.WriteTo.File(
			new DurableLogFormatter(),
			template,
			fileSizeLimitBytes: maxFileBytes,
			rollingInterval: RollingInterval.Day,
			rollOnFileSizeLimit: true,
			retainedFileCountLimit: retainedFileCount,
			retainedFileTimeLimit: RetainedFileTime);
	}

	// The undated pair an earlier version of this package wrote. Serilog's retention matches the dated template
	// and would never reach them, so the stated ceiling would be wrong by their size for the life of the house.
	private static void RemoveSuperseded(string directory, string stem)
	{
		foreach (string name in (string[])[stem + Extension, stem + ".1" + Extension])
		{
			try
			{
				File.Delete(Path.Combine(directory, name));
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// Leaving them costs disk and nothing else; failing here would cost the durable log entirely.
			}
		}
	}
}
