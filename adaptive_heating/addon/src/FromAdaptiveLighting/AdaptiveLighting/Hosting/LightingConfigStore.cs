using System.Text;

namespace Adaptive.Settings;

/// <summary>The one file the heating settings page is allowed to write, and the only way it writes it.</summary>
/// <remarks>
///     The path is resolved once, server-side, and is immutable. Nothing on the write path takes a path or a
///     fragment of one from a request. Writes go to a temp file and are moved into place, so a process death
///     mid-write cannot leave a half-written config; one previous generation is kept as <c>.bak</c>.
///     Bytes only: normalising and validating belong to <see cref="Adaptive.Engine.HeatingDocumentHost"/>, which is the only caller
///     of <see cref="Write"/>.
/// </remarks>
public sealed class HeatingConfigStore
{
	private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

	private readonly ILogger<HeatingConfigStore> _logger;

	// Only about the bytes on disk. The engine rebuild that follows a save is guarded separately, in
	// HeatingDocumentHost.
	private readonly Lock _gate = new();

	/// <summary>Creates a store over one fixed file. The host resolves <c>filePath</c> server-side.</summary>
	public HeatingConfigStore(string filePath, ILogger<HeatingConfigStore> logger)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

		_logger = logger ?? throw new ArgumentNullException(nameof(logger));

		FilePath = Path.GetFullPath(filePath);
		BackupPath = FilePath + ".bak";
	}

	/// <summary>The document's absolute path. A path, not a secret: safe to show in the UI.</summary>
	public string FilePath { get; }

	/// <summary>Where <see cref="Write"/> leaves the previous version.</summary>
	public string BackupPath { get; }

	/// <summary>Whether the document exists yet.</summary>
	public bool Exists => File.Exists(FilePath);

	/// <summary>Whether a previous version is on disk to fall back to by hand.</summary>
	public bool HasBackup => File.Exists(BackupPath);

	/// <summary>When the document was last written, or <c>null</c> when it does not exist.</summary>
	public DateTimeOffset? LastWrittenUtc => Exists ? File.GetLastWriteTimeUtc(FilePath) : null;

	/// <exception cref="HeatingConfigException">The file is missing, unreadable, or not a valid document.</exception>
	public HeatingConfig Load() => Read().Config;

	/// <summary>
	///     Reads the document and reports whether it had to be translated out of the pre-2.0 schema to be read at all.
	///     Only <see cref="Adaptive.Engine.HeatingDocumentHost.Reload"/> acts on the flag.
	/// </summary>
	/// <exception cref="HeatingConfigException">The file is missing, unreadable, or not a valid document.</exception>
	public DocumentReadResult Read()
	{
		lock (_gate)
		{
			string text;

			try
			{
				text = File.ReadAllText(FilePath);
			}
			catch (FileNotFoundException exception)
			{
				throw new HeatingConfigException(
					$"No configuration file at '{FilePath}'. The host resolves this path from its NetDaemon application configuration folder.",
					exception);
			}
			catch (DirectoryNotFoundException exception)
			{
				throw new HeatingConfigException(
					$"No configuration file at '{FilePath}': the directory does not exist.", exception);
			}
			catch (IOException exception)
			{
				throw new HeatingConfigException($"Could not read '{FilePath}': {exception.Message}", exception);
			}
			catch (UnauthorizedAccessException exception)
			{
				throw new HeatingConfigException($"Not allowed to read '{FilePath}': {exception.Message}", exception);
			}

			return HeatingConfigDocument.Deserialize(text, _logger);
		}
	}

	/// <summary>Writes <paramref name="config"/>, keeping one backup of what was there.</summary>
	/// <remarks>
	///     Whoever calls this has already normalised and validated: that is the host's one write step, and it is the
	///     only caller, because a save re-reads the file and rebuilds every area afterwards.
	/// </remarks>
	/// <exception cref="HeatingConfigException">The file could not be written.</exception>
	internal void Write(HeatingConfig config)
	{
		ArgumentNullException.ThrowIfNull(config);

		// The file as it stands, so a key this build does not read is carried into the text written rather than
		// dropped. Read outside the gate: a failure here is no reason to refuse the write.
		string? standing = null;

		try
		{
			if (File.Exists(FilePath))
				standing = File.ReadAllText(FilePath);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			_logger.LogWarning(exception, "Could not read {Path} before writing it, so any key this build does not read is dropped.", FilePath);
		}

		string yaml = HeatingConfigDocument.Serialize(config, standing);

		lock (_gate)
		{
			// GetRandomFileName, not a fixed ".tmp": on a fixed name two racing saves truncate each other, which is
			// the failure the temp file exists to prevent.
			string directory = Path.GetDirectoryName(FilePath)
				?? throw new HeatingConfigException($"'{FilePath}' has no directory to write into.");
			string temporary = Path.Combine(directory, $".{Path.GetFileName(FilePath)}.{Path.GetRandomFileName()}.tmp");

			try
			{
				Directory.CreateDirectory(directory);

				// Flushed to disk before the replace: on a power cut the rename can otherwise reach disk before the data.
				using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
				{
					stream.Write(Utf8NoBom.GetBytes(yaml));
					stream.Flush(flushToDisk: true);
				}

				if (File.Exists(FilePath))
					// One call: replace the target and move the old contents to the backup, leaving no window in
					// which the backup is the only copy.
					File.Replace(temporary, FilePath, BackupPath, ignoreMetadataErrors: true);
				else
					File.Move(temporary, FilePath);

				_logger.LogInformation(
					"Wrote heating configuration to {Path} ({Areas} areas, {Periods} periods). Previous version kept at {Backup}.",
					FilePath, config.Areas.Count, config.Periods.Count, BackupPath);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				TryDelete(temporary);
				throw new HeatingConfigException($"Could not write '{FilePath}': {exception.Message}", exception);
			}
		}
	}

	private void TryDelete(string path)
	{
		try
		{
			if (File.Exists(path))
				File.Delete(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// A leftover temp file is litter, not a failure, and it must not mask the real write error.
			_logger.LogWarning(exception, "Could not remove the temporary file {Path}.", path);
		}
	}
}
