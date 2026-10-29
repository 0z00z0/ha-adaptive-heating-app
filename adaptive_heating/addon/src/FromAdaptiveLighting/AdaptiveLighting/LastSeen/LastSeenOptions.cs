using AdaptiveLighting.Configuration;

namespace Adaptive.Engine;

/// <summary>The knobs behind <see cref="LastSeenTracker"/>, none of them exposed in the configuration document.</summary>
public sealed class LastSeenOptions
{
	/// <summary>How often the whole entity population is sampled.</summary>
	public TimeSpan CensusInterval { get; init; } = TimeSpan.FromSeconds(60);

	/// <summary>How often changed records are written to disk.</summary>
	/// <remarks>A hard kill costs this much freshness, never history.</remarks>
	public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMinutes(5);

	/// <summary>How long after Home Assistant starts a timestamp is refused as restore evidence.</summary>
	/// <remarks>Refusing a real report costs minutes of staleness; accepting a restore declares a dead sensor fresh, so err long.</remarks>
	public TimeSpan StartupGrace { get; init; } = TimeSpan.FromMinutes(5);

	/// <summary>How tightly the population's timestamps must cluster before it counts as a restart restore.</summary>
	/// <remarks>A running house spreads timestamps over hours or days; nothing survives a restart with an older one.</remarks>
	public TimeSpan CollapseWindow { get; init; } = TimeSpan.FromMinutes(10);

	/// <summary>The smallest population the collapse test is allowed to draw a conclusion from.</summary>
	/// <remarks>
	///     Below this the test abstains and only the homeassistant_start event detects a restart; a handful of chatty
	///     sensors would otherwise satisfy the collapse test permanently.
	/// </remarks>
	public int MinimumPopulation { get; init; } = 10;

	/// <summary>How long a record outlives the entity it belongs to.</summary>
	/// <remarks>Applies only to entities Home Assistant has stopped reporting; a quiet device is never dropped.</remarks>
	public TimeSpan Retention { get; init; } = TimeSpan.FromDays(30);

	/// <summary>The ceiling on tracked entities, enforced by dropping the oldest absent ones early.</summary>
	/// <remarks>Only absent entities count against it; dropping a present one would have the next census re-add it.</remarks>
	public int MaxTracked { get; init; } = 5000;

	// Lighting's own defaults as values: the heating document carries no motion or illuminance settings, and the
	// buckets only sort the cache into files.

	/// <summary>The label that files an entity as motion, the engine's escape hatch for odd presence hardware.</summary>
	public string MotionLabel { get; init; } = "adaptive_motion";

	/// <summary>The binary-sensor device classes filed as motion.</summary>
	public IReadOnlyList<string> MotionDeviceClasses { get; init; } = ["motion", "occupancy", "presence"];

	/// <summary>The sensor device class filed as illuminance.</summary>
	public string IlluminanceDeviceClass { get; init; } = "illuminance";
}
