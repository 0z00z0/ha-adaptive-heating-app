using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace Adaptive.Engine;

/// <summary>The one way every engine-owned JSON file is written and read: whole, through a temporary name, flushed to disk.</summary>
// One copy, because three hand-written ones drifted: only the configuration document used to flush before the
// rename, so a power cut could leave a note or a cache bucket back as an empty file.
internal static class AtomicJsonFile
{
	public const string BackupSuffix = ".bak";

	private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

	/// <summary>Writes <paramref name="document"/> to <paramref name="path"/>, optionally keeping the previous file as the backup.</summary>
	/// <remarks>Reports failure and never throws: every caller runs on a timer or an event and must cost state, never the engine.</remarks>
	public static bool TryWrite<TDocument>(
		string path,
		TDocument document,
		JsonSerializerOptions options,
		bool keepBackup,
		[NotNullWhen(false)] out Exception? failure)
	{
		string directory = Path.GetDirectoryName(path) ?? ".";

		// A random temp name, not a fixed ".tmp": two writers on a fixed name truncate each other.
		string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Path.GetRandomFileName()}.tmp");

		try
		{
			Directory.CreateDirectory(directory);
			WriteAndFlush(temporary, JsonSerializer.Serialize(document, options));

			if (keepBackup && File.Exists(path))
				// One call. Copying to .bak first leaves a window where the backup is the only copy.
				File.Replace(temporary, path, path + BackupSuffix, ignoreMetadataErrors: true);
			else
				File.Move(temporary, path, overwrite: true);

			failure = null;
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			TryDelete(temporary);

			failure = exception;
			return false;
		}
	}

	/// <summary>Reads one file. A missing file succeeds with no document; an unreadable one fails with the reason.</summary>
	public static bool TryRead<TDocument>(
		string path,
		JsonSerializerOptions options,
		out TDocument? document,
		[NotNullWhen(false)] out Exception? failure)
		where TDocument : class
	{
		document = null;
		failure = null;

		try
		{
			if (!File.Exists(path))
				return true;

			document = JsonSerializer.Deserialize<TDocument>(File.ReadAllText(path), options);
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
		{
			failure = exception;
			return false;
		}
	}

	/// <summary>Removes a file, reporting failure and never throwing.</summary>
	public static bool TryRemove(string path, [NotNullWhen(false)] out Exception? failure)
	{
		try
		{
			if (File.Exists(path))
				File.Delete(path);

			failure = null;
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			failure = exception;
			return false;
		}
	}

	/// <summary>Deletes a leftover temporary file, which is litter and must never mask the write error that left it.</summary>
	public static void TryDelete(string path)
	{
		try
		{
			if (File.Exists(path))
				File.Delete(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Litter, not a failure.
		}
	}

	// The rename can reach the disk ahead of the bytes, so without the flush a power cut can bring the file back empty.
	private static void WriteAndFlush(string path, string text)
	{
		byte[] bytes = Utf8NoBom.GetBytes(text);

		using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
		stream.Write(bytes, 0, bytes.Length);
		stream.Flush(flushToDisk: true);
	}
}
