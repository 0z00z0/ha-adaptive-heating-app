using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Adaptive.Engine;

/// <summary>One small machine-written JSON note beside the configuration document, read and written without throwing.</summary>
// The configuration document's directory is the only one on a Home Assistant box that survives a redeploy.
// Not thread-safe: each store holds its own lock around every call.
internal sealed class JsonNoteFile
{
	// The header every note starts with, ordered first so it is the first thing a reader of the file sees.
	public const string CommentProperty = "_comment";
	public const int CommentOrder = -2;
	public const string VersionProperty = "version";
	public const int VersionOrder = -1;
	public const string SavedAtProperty = "savedAt";

	public const string BackupSuffix = AtomicJsonFile.BackupSuffix;

	/// <summary>The subdirectory the notes live in, so the document's own folder holds the one hand-edited file.</summary>
	public const string StateFolderName = "state";

	private const string FallbackStem = "adaptive-heating";

	private readonly ILogger _logger;
	private readonly bool _keepsBackup;

	/// <summary>Places the note under <paramref name="configFilePath"/>'s directory, named after its stem.</summary>
	/// <exception cref="ArgumentException"><paramref name="configFilePath"/> is blank or has no directory.</exception>
	public JsonNoteFile(string configFilePath, string nameSuffix, ILogger logger, bool inStateFolder = false, bool keepsBackup = true)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(configFilePath);

		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		_keepsBackup = keepsBackup;

		string full = Path.GetFullPath(configFilePath);

		DocumentDirectoryPath = Path.GetDirectoryName(full)
			?? throw new ArgumentException($"'{configFilePath}' has no directory to write beside.", nameof(configFilePath));

		DirectoryPath = inStateFolder ? Path.Combine(DocumentDirectoryPath, StateFolderName) : DocumentDirectoryPath;

		// The stem, so home.yaml gets home + suffix and two hosts sharing a directory cannot collide.
		string stem = Path.GetFileNameWithoutExtension(full);
		if (stem.Length == 0)
			stem = FallbackStem;

		FileName = stem + nameSuffix;
		FilePath = Path.Combine(DirectoryPath, FileName);

		if (inStateFolder)
			AdoptFileWrittenBesideTheDocument();
	}

	/// <summary>The configuration document's own directory.</summary>
	public string DocumentDirectoryPath { get; }

	/// <summary>The directory the note lives in, which is the state subfolder unless the note stays beside the document.</summary>
	public string DirectoryPath { get; }

	public string FileName { get; }

	public string FilePath { get; }

	public string BackupPath => FilePath + BackupSuffix;

	/// <summary>Serializer options for a note: indented because a person is expected to open the file.</summary>
	public static JsonSerializerOptions CreateSerializerOptions() => new()
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		// A hand-edited file, or one from a build that knew a field this one does not, must not cost the note.
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true
	};

	/// <summary>Reads the note, falling back to the backup. A missing pair succeeds with no document.</summary>
	/// <remarks>
	///     The backup is read when the main file is missing as well as when it will not parse: a kill between the two
	///     steps of a replace leaves only the backup, measured at 1.5 % of kills on Windows.
	/// </remarks>
	public bool TryRead<TDocument>(
		JsonSerializerOptions options,
		out TDocument? document,
		out bool fromBackup,
		[NotNullWhen(false)] out Exception? failure)
		where TDocument : class
	{
		fromBackup = false;

		bool readMain = AtomicJsonFile.TryRead(FilePath, options, out document, out Exception? mainFailure);

		if (readMain && document is not null)
		{
			failure = null;
			return true;
		}

		if (_keepsBackup
			&& AtomicJsonFile.TryRead(BackupPath, options, out TDocument? backup, out _)
			&& backup is not null)
		{
			document = backup;
			fromBackup = true;
			failure = null;

			_logger.LogWarning(
				mainFailure,
				"Read {Backup} because {Path} is missing or could not be parsed. The backup holds the last completed "
				+ "write, so at most the newest change is lost.",
				BackupPath, FilePath);

			return true;
		}

		// Nothing anywhere is a first run, and not a fault.
		failure = mainFailure;
		return mainFailure is null;
	}

	/// <summary>Writes the note in one step, keeping the previous one as the backup.</summary>
	public bool TryWrite<TDocument>(TDocument document, JsonSerializerOptions options, [NotNullWhen(false)] out Exception? failure) =>
		AtomicJsonFile.TryWrite(FilePath, document, options, _keepsBackup, out failure);

	/// <summary>Deletes the note and its backup.</summary>
	public bool TryRemove([NotNullWhen(false)] out Exception? failure)
	{
		if (!AtomicJsonFile.TryRemove(FilePath, out failure))
			return false;

		return AtomicJsonFile.TryRemove(BackupPath, out failure);
	}

	/// <summary>Moves a note an earlier build wrote beside the document into the state subfolder.</summary>
	/// <remarks>Failures are ignored: giving up costs the note, which reads as unknown, and must never stop start-up.</remarks>
	private void AdoptFileWrittenBesideTheDocument()
	{
		int moved = Adopt(FileName) + Adopt(FileName + BackupSuffix);

		if (moved > 0)
			_logger.LogInformation(
				"Moved {Count} file(s) for the {Name} note into {Directory}. The configuration document and its backup "
				+ "are what sit beside it now.",
				moved, FileName, DirectoryPath);
	}

	private int Adopt(string name)
	{
		string stray = Path.Combine(DocumentDirectoryPath, name);
		string destination = Path.Combine(DirectoryPath, name);

		try
		{
			if (!File.Exists(stray))
				return 0;

			// The copy already in the state folder is the live one, so a stray beside the document is only ever stale.
			if (File.Exists(destination))
			{
				File.Delete(stray);
				return 1;
			}

			Directory.CreateDirectory(DirectoryPath);
			File.Move(stray, destination);

			return 1;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			_logger.LogDebug(exception, "Could not move {Path} into {Directory}; the next start tries again.", stray, DirectoryPath);
			return 0;
		}
	}
}
