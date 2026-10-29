using Adaptive.Settings;

namespace AdaptiveHeating.AddOn.Integration;

/// <summary>Warns once for each room whose power sensor discovery cannot decide, until one is picked.</summary>
internal sealed class PowerSensorsToPick
{
	private readonly ILogger _logger;

	// Room and candidates: a room that settles and later finds two again is warned again.
	private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

	public PowerSensorsToPick(ILogger logger) => _logger = logger ?? throw new ArgumentNullException(nameof(logger));

	/// <returns>The rooms warned on this call.</returns>
	public IReadOnlyList<string> Check(HeatingConfig document, AreaEntityResolver resolver)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(resolver);

		HashSet<string> standing = new(StringComparer.Ordinal);
		List<string> warned = [];

		foreach (AreaConfig area in document.Areas)
		{
			if (!resolver.TryResolve(area, document.Defaults, out ResolvedRoom? room, out _)
				|| room.PowerSensorFoundBy is not PowerSensorSource.MoreThanOne)
				continue;

			string candidates = string.Join(", ", room.PowerSensorCandidates);
			string key = $"{area.AreaId}|{candidates}";

			standing.Add(key);

			if (!_warned.Add(key))
				continue;

			warned.Add(room.Name);
			_logger.LogWarning(
				"Room {Room} has {Count} power sensors that could each be its meter ({Candidates}), so it uses none. "
				+ "Pick one on the room page.",
				room.Name, room.PowerSensorCandidates.Count, candidates);
		}

		_warned.IntersectWith(standing);

		return warned;
	}
}
