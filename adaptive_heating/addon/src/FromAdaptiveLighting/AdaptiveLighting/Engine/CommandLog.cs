using System.Globalization;
using System.Text;

using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Engine;

/// <summary>Writes every command the add-on sends a thermostat: a file per thermostat, a line per command.</summary>
/// <remarks>Written only while the document's <see cref="GlobalConfig.CommandLog"/> is on.</remarks>
public sealed class CommandLog
{
	/// <summary>The folder beside the document that holds the durable log and this one.</summary>
	public const string LogFolderName = "log";

	public const string FolderName = "commands";

	/// <summary>Where a house-wide command is written, since it belongs to no one room.</summary>
	public const string HouseFolder = "_house";

	private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

	private readonly string _directory;
	private readonly ILogger _logger;
	private readonly Lock _gate = new();
	private bool _failureReported;

	public CommandLog(string directory, ILogger logger)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);

		_directory = directory;
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <summary>The log's directory for the document at <paramref name="documentPath"/>: <c>log/commands</c> beside it.</summary>
	public static string DirectoryBeside(string documentPath) =>
		Path.Combine(Path.GetDirectoryName(Path.GetFullPath(documentPath)) ?? ".", LogFolderName, FolderName);

	/// <summary>Lower case, with everything outside <c>a-z 0-9 . _ -</c> as <c>_</c>.</summary>
	public static string SafeName(string name)
	{
		StringBuilder safe = new(name.Length);

		foreach (char c in name.ToLowerInvariant())
			safe.Append(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-' ? c : '_');

		// A name of dots alone would climb out of the folder.
		return safe.Length == 0 || safe.ToString().Trim('.').Length == 0 ? "_" : safe.ToString();
	}

	/// <summary>One command to <paramref name="thermostat"/>, or to the house where it is <c>null</c>.</summary>
	// Formatting runs inside the catch as well, so nothing about a line can reach the heating path.
	public void Write(string? thermostat, Func<string> line)
	{
		lock (_gate)
		{
			try
			{
				string directory = thermostat is { Length: > 0 } ? _directory : Path.Combine(_directory, HouseFolder);
				string path = Path.Combine(directory, SafeName(thermostat ?? "labels") + ".log");
				string text = line() + "\n";

				if (!File.Exists(path))
				{
					Directory.CreateDirectory(directory);
					text = "# " + (thermostat ?? "House-wide commands") + "\n" + text;
				}

				File.AppendAllText(path, text, Utf8NoBom);
			}
			catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
			{
				if (_failureReported)
					return;

				_failureReported = true;
				_logger.LogWarning(exception,
					"Could not write the thermostat command log under {Directory}. Heating carries on; further failures are not reported until the next restart.",
					_directory);
			}
		}
	}

	/// <summary>One line: when, the action, its fields in order, and what came of it.</summary>
	public static string Line(DateTimeOffset at, string action, IReadOnlyDictionary<string, object?> data, string outcome)
	{
		StringBuilder line = new StringBuilder()
			.Append(at.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture))
			.Append(" action=").Append(action);

		foreach (string key in data.Keys.Order(StringComparer.Ordinal))
			if (data[key] is { } value)
				AppendPair(line, key, value);

		return line.Append(" outcome=").Append(outcome).ToString();
	}

	private static void AppendPair(StringBuilder line, string key, object value) =>
		line.Append(' ').Append(key).Append('=').Append(value switch
		{
			IEnumerable<string> many => string.Join(",", many),
			IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
			_ => value.ToString()
		});
}
