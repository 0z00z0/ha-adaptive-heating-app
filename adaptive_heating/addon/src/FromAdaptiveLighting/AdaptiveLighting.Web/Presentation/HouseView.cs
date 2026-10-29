using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Web.Presentation;

/// <summary>The decisions the House tab makes about how the house is written down, in one testable place.</summary>
/// <remarks>Nothing here reads or writes the document; the tab mutates its own copy and hands it to the save pipeline.</remarks>
public static class HouseView
{
	/// <summary>How many straying rooms are named before the line falls back to counting them.</summary>
	private const int NamedRooms = 3;

	// null makes Blazor drop the attribute, leaving no link and no tab stop.
	public static string? RoomHref(string? areaId) =>
		areaId is { Length: > 0 } id ? $"room/{Uri.EscapeDataString(id)}" : null;

	public static string RoomSummary(AreaConfig area)
	{
		ArgumentNullException.ThrowIfNull(area);

		// Through AreaSetupService.OverrideCount, never a list of its own: a local list misses settings, and a room
		// tuned only through those reports "all automatic".
		int changed = AreaSetupService.OverrideCount(area);
		string pinned = PinnedSummary(area);

		return (changed, pinned.Length) switch
		{
			(0, 0) => "all automatic",
			(0, _) => $"{pinned} picked by hand",
			(_, 0) => $"{changed} of {AreaView.OverridableSettingCount} changed",
			_ => $"{pinned} picked by hand · {changed} of {AreaView.OverridableSettingCount} changed"
		};
	}

	/// <summary>What a room lists instead of discovering, empty when it discovers everything.</summary>
	public static string PinnedSummary(AreaConfig area)
	{
		ArgumentNullException.ThrowIfNull(area);

		List<string> parts = new(4);

		if (area.Heaters is { Count: > 0 } heaters)
			parts.Add(Count(heaters.Count, "heater"));

		if (area.TemperatureSensors is { Count: > 0 } sensors)
			parts.Add(Count(sensors.Count, "temperature sensor"));

		if (area.WindowSensor is { Length: > 0 })
			parts.Add("window sensor");

		if (area.PauseWhenOn is { Count: > 0 } blockers)
			parts.Add(Count(blockers.Count, "blocker"));

		return string.Join(", ", parts);
	}

	public static string? SwitchedOffLine(IEnumerable<AreaConfig> areas, AreaSettings defaults)
	{
		ArgumentNullException.ThrowIfNull(areas);
		ArgumentNullException.ThrowIfNull(defaults);

		int off = areas.Count(area => !AreaView.IsEnabled(area, defaults));

		return off switch
		{
			<= 0 => null,
			1 => "1 room off — never changes by itself",
			_ => $"{off} rooms off — never change by themselves"
		};
	}

	/// <summary>The Home Assistant areas no room has claimed, so two rooms cannot fight over the same heaters.</summary>
	public static IReadOnlyList<AreaOption> Unconfigured(IEnumerable<AreaOption> areas, IEnumerable<AreaConfig> configured)
	{
		ArgumentNullException.ThrowIfNull(areas);
		ArgumentNullException.ThrowIfNull(configured);

		HashSet<string> taken =
		[
			.. configured.Select(room => room.AreaId).OfType<string>().Where(areaId => areaId.Length > 0)
		];

		return [.. areas.Where(area => !taken.Contains(area.Id))];
	}

	// AreaNaming's order, so the rows, the board's lanes and the room page's heading name one room one way.
	public static string DisplayName(AreaConfig area, IAreaRegistry? registry)
	{
		ArgumentNullException.ThrowIfNull(area);

		return AreaNaming.Resolve(area, registry) ?? "New room";
	}

	/// <summary>A handful of names written as English writes a list, falling back to a count once it would be a wall.</summary>
	public static string NameList(IReadOnlyList<string> names)
	{
		ArgumentNullException.ThrowIfNull(names);

		if (names.Count == 0)
			return string.Empty;

		if (names.Count == 1)
			return names[0];

		if (names.Count <= NamedRooms)
			return $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";

		int rest = names.Count - 2;

		return $"{names[0]}, {names[1]} and {rest} others";
	}

	private static string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";
}
