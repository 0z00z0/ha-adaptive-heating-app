using System.Text.Json;
using System.Text.Json.Serialization;

using AdaptiveHeating.Page.Settings;

namespace AdaptiveHeating.AddOn.Settings;

/// <summary>The settings as they sit in the file: what wrote them, when, and the document itself.</summary>
internal sealed record StoredSettings(int Version, DateTimeOffset SavedAt, HeatingDocument? Settings)
{
	/// <summary>What this build writes. A file from a newer build is left alone rather than half-read.</summary>
	public const int CurrentVersion = 1;

	/// <summary>
	/// Reads and writes the settings file. Unknown properties are ignored, which is what lets a file written
	/// by a build that knew a field this one does not still restore everything this one does know.
	/// </summary>
	public static JsonSerializerOptions SerializerOptions { get; } = Build();

	private static JsonSerializerOptions Build()
	{
		JsonSerializerOptions options = JsonNoteFile.CreateSerializerOptions();

		// The mode is a dictionary key as well as a value, so the names in the file read as Away and Night
		// rather than as 0 and 1, and a mode added in the middle cannot silently renumber the rest.
		options.Converters.Add(new JsonStringEnumConverter());

		return options;
	}
}
