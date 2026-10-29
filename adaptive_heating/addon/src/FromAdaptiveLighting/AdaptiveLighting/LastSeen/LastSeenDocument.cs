using System.Text.Json;
using System.Text.Json.Serialization;

namespace Adaptive.Engine;

/// <summary>One entity's line in the cache.</summary>
/// <param name="LastSeen">When there was last trustworthy evidence the entity is alive, or <c>null</c> for never.</param>
/// <param name="TrackedSince">When the entity was first met; ageing and eviction fall back to it with no last-seen time.</param>
public sealed record LastSeenEntry(
	[property: JsonPropertyName("lastSeen")] DateTimeOffset? LastSeen,
	[property: JsonPropertyName("trackedSince")] DateTimeOffset TrackedSince);

/// <summary>One cache file: the records for a single bucket, plus enough context to read it unaided.</summary>
/// <remarks>Lives in the configuration document's directory; the deploy folder is wiped and re-copied every redeploy.</remarks>
public sealed class LastSeenDocument
{
	public const string Explanation =
		"Machine-written cache: when Adaptive Heating last had trustworthy evidence that each Home Assistant entity "
		+ "is alive. Home Assistant resets its own last_updated on every restart, so this is kept separately. "
		+ "Not configuration - nothing here is edited by hand, and deleting this file is safe: the only cost is the "
		+ "history, and every entity in it reverts to 'unknown' until it reports again.";

	/// <summary>The current document version, bumped only when an older file could be misread.</summary>
	/// <remarks>Version 1's <c>other</c> is a pile awaiting redistribution; version 2's is a real bucket.</remarks>
	public const int CurrentVersion = 2;

	/// <summary>The version whose <c>other</c> file held everything that was not a light, motion or illuminance.</summary>
	public const int PreSplitVersion = 1;

	[JsonPropertyName("_comment")]
	[JsonPropertyOrder(-3)]
	public string Comment { get; set; } = Explanation;

	[JsonPropertyName("version")]
	[JsonPropertyOrder(-2)]
	public int Version { get; set; } = CurrentVersion;

	/// <summary>Which bucket this file holds: a device class, a domain, or one of the three curated names.</summary>
	/// <remarks>
	///     The only place the unsanitised key survives, since the file name may be fingerprinted. Null means the file
	///     did not say, which the loader treats differently from a file that says <c>other</c>.
	/// </remarks>
	[JsonPropertyName("kind")]
	[JsonPropertyOrder(-1)]
	public string? Bucket { get; set; }

	/// <summary>When this file was written, and the tie-breaker when an entity turns up in two files.</summary>
	[JsonPropertyName("savedAt")]
	public DateTimeOffset SavedAt { get; set; }

	/// <summary>The estimate of when Home Assistant last started, in every file so no bucket depends on another.</summary>
	/// <remarks>It moves forwards only, so the loader takes the newest value across all files.</remarks>
	[JsonPropertyName("homeAssistantStarted")]
	public DateTimeOffset? HomeAssistantStarted { get; set; }

	/// <summary>The records, keyed by entity id and sorted so a diff between two versions is readable.</summary>
	[JsonPropertyName("entities")]
	public SortedDictionary<string, LastSeenEntry> Entities { get; set; } = new(StringComparer.Ordinal);

	/// <summary>How the cache files are written and read, indented because a person is expected to open them.</summary>
	public static readonly JsonSerializerOptions SerializerOptions = new()
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		// A hand-edited file, or one from a build that knew a field this one does not, must not cost the history.
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true
	};
}
