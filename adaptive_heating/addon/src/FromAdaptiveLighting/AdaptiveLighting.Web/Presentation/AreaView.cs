using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace Adaptive.UI;

/// <summary>The decisions the two area screens make about how a room is shown, in one testable place.</summary>
/// <remarks>The dashboard's cards and the settings list share this colour mapping, so one house is painted once.</remarks>
public static class AreaView
{
	// Must equal what AreaSetupService.OverrideCount counts. Enabled is not in the list, since the header toggle
	// owns it.
	public const int OverridableSettingCount = 5;

	/// <summary>What a floorless group is called, once, so both screens head it the same way.</summary>
	public const string FloorlessTitle = "Other rooms";

	/// <summary>Whether the engine may command this room, following the document's inheritance.</summary>
	public static bool IsEnabled(AreaConfig area, AreaSettings defaults)
	{
		ArgumentNullException.ThrowIfNull(area);
		ArgumentNullException.ThrowIfNull(defaults);

		return area.Enabled ?? defaults.Enabled;
	}

	/// <summary>Whether every room in a group is switched on, which flips the floor's bulk action.</summary>
	/// <remarks>An empty group counts as not-all-on, so the button never offers to switch off nothing.</remarks>
	public static bool AllEnabled(IEnumerable<AreaConfig> areas, AreaSettings defaults)
	{
		ArgumentNullException.ThrowIfNull(areas);
		ArgumentNullException.ThrowIfNull(defaults);

		List<AreaConfig> rooms = [.. areas];

		return rooms.Count > 0 && rooms.TrueForAll(area => IsEnabled(area, defaults));
	}

	/// <summary>Switches a whole floor on or off, in the in-memory document. Nothing here writes to disk.</summary>
	/// <returns>How many rooms actually changed value, so a caller can skip a no-op.</returns>
	public static int SwitchAll(IEnumerable<AreaConfig> areas, bool on)
	{
		ArgumentNullException.ThrowIfNull(areas);

		int changed = 0;

		// Explicit true/false on every room, never null: a decision made with a button is one the file should state.
		foreach (AreaConfig area in areas)
		{
			if (area.Enabled != on)
				changed++;

			area.Enabled = on;
		}

		return changed;
	}

	/// <summary>Whether a floor group earns a header. A house with no floors collapses to one unheaded group.</summary>
	/// <param name="floor">The group's floor, or <c>null</c> for the trailing floorless group.</param>
	public static bool ShowsHeader(int groupCount, AreaFloor? floor) => groupCount > 1 || floor is not null;

	public static string FloorTitle(AreaFloor? floor) => floor?.Name ?? FloorlessTitle;

	// ===================== the dashboard =====================

	/// <summary>
	///     Whether the dashboard shows the first-run state instead of a grid: rooms exist, none is switched on,
	///     and Home Assistant is answering.
	/// </summary>
	/// <param name="engineIsAttached">
	///     Whether the engine has a Home Assistant connection. A disconnected host also shows no warm rooms, and
	///     onboarding offered there would hide that.
	/// </param>
	public static bool IsAwaitingRoomChoice(IEnumerable<AreaConfig> rooms, AreaSettings defaults, bool engineIsAttached)
	{
		ArgumentNullException.ThrowIfNull(rooms);
		ArgumentNullException.ThrowIfNull(defaults);

		List<AreaConfig> configured = [.. rooms];

		return engineIsAttached && configured.Count > 0 && configured.TrueForAll(room => !IsEnabled(room, defaults));
	}

	/// <summary>The live reports the dashboard gives a lane to: those from rooms switched on in the document.</summary>
	// Matched by area id first and display name second, as the snapshot cache and the settings list match.
	public static IReadOnlyList<AreaSnapshot> VisibleCards(IEnumerable<AreaSnapshot> snapshots, IEnumerable<RoomView> rooms)
	{
		ArgumentNullException.ThrowIfNull(snapshots);
		ArgumentNullException.ThrowIfNull(rooms);

		Dictionary<string, bool> byId = new(StringComparer.Ordinal);
		Dictionary<string, bool> byName = new(StringComparer.OrdinalIgnoreCase);

		foreach (RoomView room in rooms)
		{
			if (room.AreaId is { Length: > 0 } areaId)
				byId[areaId] = room.IsEnabled;

			byName[room.Name] = room.IsEnabled;
		}

		return [.. snapshots.Where(snapshot => IsShown(snapshot, byId, byName))];
	}

	/// <summary>How many rooms are switched off, counted off the document and not off the lanes.</summary>
	public static int SwitchedOffCount(IEnumerable<RoomView> rooms)
	{
		ArgumentNullException.ThrowIfNull(rooms);

		return rooms.Count(room => !room.IsEnabled);
	}

	/// <summary>The footer line for the hidden rooms, or <c>null</c> when nothing is hidden.</summary>
	public static HiddenRoomsNote? HiddenNote(int switchedOff) => switchedOff switch
	{
		<= 0 => null,
		1 => new HiddenRoomsNote("1 room switched off —", "switch on"),
		_ => new HiddenRoomsNote($"{switchedOff} rooms switched off —", "switch on")
	};

	// A report matching no room is shown, never hidden: a lane dropped because the document changed underneath it
	// is indistinguishable from a fault.
	private static bool IsShown(AreaSnapshot snapshot, Dictionary<string, bool> byId, Dictionary<string, bool> byName)
	{
		if (snapshot.AreaId is { Length: > 0 } areaId && byId.TryGetValue(areaId, out bool enabledById))
			return enabledById;

		return !byName.TryGetValue(snapshot.AreaName, out bool enabledByName) || enabledByName;
	}
}

/// <summary>The dashboard's line about switched-off rooms, and the words its link carries.</summary>
public sealed record HiddenRoomsNote(string Lead, string LinkText);
