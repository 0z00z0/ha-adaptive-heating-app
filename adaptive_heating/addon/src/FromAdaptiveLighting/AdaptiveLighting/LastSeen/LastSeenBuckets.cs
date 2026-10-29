using System.Security.Cryptography;
using System.Text;

namespace Adaptive.Engine;

/// <summary>Decides which file an entity's record belongs in, and turns that decision into a file name.</summary>
/// <remarks>
///     Bucket keys are strings because a custom integration can declare any device class. The rules mirror the
///     engine's room resolver instead of calling it, so a change there does not re-file the cache.
/// </remarks>
public static class LastSeenBuckets
{
	private const string LightDomain = "light";
	private const string BinarySensorDomain = "binary_sensor";
	private const string SensorDomain = "sensor";

	public const string Light = "light";

	public const string Motion = "motion";

	public const string Illuminance = "illuminance";

	/// <summary>Where an entity with no device class and no usable domain is filed.</summary>
	/// <remarks>The value is the pre-split catch-all name, so an upgrading install's file is re-keyed, not orphaned.</remarks>
	public const string Unclassified = "other";

	// Never stands alone: always carries the key's fingerprint, so two unrepresentable keys do not share a file.
	private const string UnnamedToken = "unnamed";

	// A device class reaches the file system, so its length is untrusted: longer keys are truncated and fingerprinted.
	private const int MaxTokenLength = 48;

	/// <summary>The buckets filed by rule instead of by device class, in file order.</summary>
	public static readonly IReadOnlyList<string> Curated = [Illuminance, Motion, Light];

	private static readonly HashSet<string> CuratedSet = new(Curated, StringComparer.Ordinal);

	public static bool IsCurated(string? bucket) => bucket is not null && CuratedSet.Contains(bucket);

	/// <summary>Works out which bucket <paramref name="entityId"/> belongs in; never throws, never returns empty.</summary>
	/// <remarks>
	///     The motion label is checked before the motion device class: the label exists for hardware whose class is
	///     wrong. <paramref name="labels"/> carries each label by id and by name, so a document storing either files.
	/// </remarks>
	public static string Classify(
		string? entityId,
		string? deviceClass,
		IEnumerable<string>? labels,
		LastSeenOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);

		if (entityId is not { Length: > 0 })
			return Unclassified;

		if (entityId.HasDomain(LightDomain))
			return Light;

		if (labels is not null
			&& options.MotionLabel is { Length: > 0 } motionLabel
			&& labels.Contains(motionLabel, StringComparer.OrdinalIgnoreCase))
			return Motion;

		if (entityId.HasDomain(BinarySensorDomain)
			&& deviceClass is { Length: > 0 }
			&& options.MotionDeviceClasses.Contains(deviceClass, StringComparer.OrdinalIgnoreCase))
			return Motion;

		if (entityId.HasDomain(SensorDomain)
			&& string.Equals(deviceClass, options.IlluminanceDeviceClass, StringComparison.OrdinalIgnoreCase))
			return Illuminance;

		string domain = Normalise(entityId.Domain());
		string derived = Normalise(deviceClass);

		if (derived.Length == 0)
			derived = domain;

		if (derived.Length == 0)
			return Unclassified;

		if (!CuratedSet.Contains(derived))
			return derived;

		// Curated tokens are reserved: a binary_sensor declaring device_class light is filed as binary_sensor_light.
		return domain.Length > 0 && !string.Equals(domain, derived, StringComparison.Ordinal)
			? domain + "_" + derived
			: Unclassified;
	}

	/// <summary>A file-name-safe token for <paramref name="bucket"/>, distinct for every distinct key.</summary>
	/// <remarks>
	///     The allow-list is a-z, 0-9 and _. Dropping characters alone would collide (a/b and a\b both give ab), so an
	///     altered key gets a fingerprint appended, and '-' is kept out of the allow-list as the separator.
	/// </remarks>
	public static string FileToken(string? bucket)
	{
		string key = Normalise(bucket);

		if (key.Length == 0)
			return Unclassified;

		StringBuilder safe = new(key.Length);
		bool altered = false;

		foreach (char character in key)
			if (character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')
				safe.Append(character);
			else
				altered = true;

		if (safe.Length > MaxTokenLength)
		{
			safe.Length = MaxTokenLength;
			altered = true;
		}

		if (!altered)
			return safe.ToString();

		return (safe.Length > 0 ? safe.ToString() : UnnamedToken) + "-" + Fingerprint(key);
	}

	/// <summary>Recovers a bucket key from a file name, for a document whose own <c>kind</c> field is missing.</summary>
	/// <remarks>Only correct for a token that was never fingerprinted; the document's <c>kind</c> is the truth.</remarks>
	public static string FromToken(string? token)
	{
		string key = Normalise(token);
		return key.Length > 0 && string.Equals(FileToken(key), key, StringComparison.Ordinal) ? key : Unclassified;
	}

	/// <summary>Trimmed and lower-cased, or empty.</summary>
	/// <remarks>Case folding is load-bearing: on a case-insensitive file system Temperature and temperature share a file.</remarks>
	public static string NormaliseKey(string? raw) => Normalise(raw);

	private static string Normalise(string? value) => value is null ? string.Empty : value.Trim().ToLowerInvariant();

	// SHA-256, since the runtime's string hash is randomised per process and this lands in a file name.
	private static string Fingerprint(string key) =>
		Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 4));
}
