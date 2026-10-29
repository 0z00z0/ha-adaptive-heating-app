using Adaptive.Settings;

using AdaptiveHeating.Page.Settings;
using AdaptiveHeating.Planner;

namespace AdaptiveHeating.AddOn.Integration;

/// <summary>The settings after a pass over the Areas, and whether that pass learnt anything.</summary>
internal readonly record struct MergedRooms(HeatingDocument Document, bool Changed);

/// <summary>The rooms, read from Home Assistant's area registry through the engine's own resolver.</summary>
// The document names which Areas are rooms, keyed by the area id; the registry says what is in each one. The
// integration is told nothing about which rooms exist: it reads the same registries and reaches the same set, and
// names each room by the room id its card publishes. Every slice is keyed by that id.
internal static class RoomsFromTheAreas
{
	private static readonly Stamp Starting = Stamp.Certain(default);

	/// <summary>Each area's room id, as the integration's cards publish it.</summary>
	public static Dictionary<string, string> RoomInEachArea(IReadOnlyList<EntityState> states)
	{
		ArgumentNullException.ThrowIfNull(states);

		Dictionary<string, string> rooms = new(StringComparer.Ordinal);

		foreach (EntityState state in states)
		{
			if (state.AttrString(HeatingActions.IntegrationVersionAttribute) is { Length: > 0 }
				&& state.AttrString(HeatingActions.RoomIdAttribute) is { Length: > 0 } roomId
				&& state.AttrString(HeatingActions.AreaIdAttribute) is { Length: > 0 } areaId)
				rooms[areaId] = roomId;
		}

		return rooms;
	}

	/// <summary>One pass's fold of the rooms into the store, against the document as the host holds it.</summary>
	public static MergedRooms MergeInto(
		ISettingsStore store,
		HeatingDocumentHost host,
		IReadOnlyList<EntityState> states,
		IAreaRegistry registry,
		AreaEntityResolver resolver)
	{
		ArgumentNullException.ThrowIfNull(store);
		ArgumentNullException.ThrowIfNull(host);
		ArgumentNullException.ThrowIfNull(states);

		MergedRooms merged = default;

		// Behind the document's gate: the room page links a moved room and saves its entry in one step there.
		host.Exclusively(() =>
		{
			merged = Merge(store.Read(), host.Current, RoomInEachArea(states), registry, resolver, PlannerDefaults.Standard);

			if (merged.Changed)
				store.Receive(merged.Document with { Versions = merged.Document.Versions with { Integration = VersionIn(states) } });
		});

		return merged;
	}

	/// <summary>Folds the resolved rooms into the settings, keeping every number a person typed.</summary>
	// An area whose card is missing keeps the room linked to it, so a core restart loses nothing. One the integration
	// has named no room in yet has no slice.
	public static MergedRooms Merge(
		HeatingDocument held,
		HeatingConfig document,
		IReadOnlyDictionary<string, string> roomInArea,
		IAreaRegistry registry,
		AreaEntityResolver resolver,
		PlannerDefaults defaults)
	{
		ArgumentNullException.ThrowIfNull(held);
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(roomInArea);
		ArgumentNullException.ThrowIfNull(registry);
		ArgumentNullException.ThrowIfNull(resolver);
		ArgumentNullException.ThrowIfNull(defaults);

		List<RoomSlice> rooms = [];
		bool changed = false;

		foreach (AreaConfig area in document.Areas)
		{
			if (area.AreaId is not { Length: > 0 } areaId)
				continue;

			RoomSlice? was = roomInArea.TryGetValue(areaId, out string? roomId)
				? held.Rooms.FirstOrDefault(room => string.Equals(room.Id, roomId, StringComparison.Ordinal))
				: held.RoomIn(areaId);

			if ((roomId ?? was?.Id) is not { } id)
				continue;

			RoomSlice now = Slice(was, area, id, areaId, document, registry, resolver, defaults);

			changed |= was is null
				|| !string.Equals(was.AreaId, now.AreaId, StringComparison.Ordinal)
				|| !string.Equals(was.Name, now.Name, StringComparison.Ordinal)
				|| !string.Equals(was.Thermostat, now.Thermostat, StringComparison.Ordinal)
				|| was.NoReadingAtAll != now.NoReadingAtAll
				|| was.Batteries?.Count != now.Batteries?.Count
				|| !string.Equals(was.Sensors.BorrowsFrom, now.Sensors.BorrowsFrom, StringComparison.Ordinal);

			rooms.Add(now);
		}

		return new MergedRooms(held with { Rooms = rooms }, changed || held.Rooms.Count != rooms.Count);
	}

	/// <summary>Each resolved room's own settings, ready to travel to that room's own thermostat.</summary>
	public static IReadOnlyList<RoomRegulation> Regulation(
		HeatingDocument held,
		HeatingConfig document,
		AreaEntityResolver resolver)
	{
		ArgumentNullException.ThrowIfNull(held);
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(resolver);

		List<RoomRegulation> sending = [];

		foreach (AreaConfig area in document.Areas)
		{
			if (area.AreaId is not { Length: > 0 } areaId)
				continue;

			if (!resolver.TryResolve(area, document.Defaults, out ResolvedRoom? room, out _))
				continue;

			if (room.Thermostat is not { Length: > 0 } thermostat)
				continue;

			RoomSensorSettings sensors = held.RoomIn(areaId)?.Sensors ?? RoomSensorSettings.Standard(PlannerDefaults.Standard);

			sending.Add(new RoomRegulation(
				thermostat,
				sensors.FreshWindow,
				sensors.StuckWindow,
				sensors.Spread,
				sensors.WindowDelay,
				area.Heaters is { Count: > 0 } ? room.Heaters : null,
				area.TemperatureSensors is { Count: > 0 } ? room.TemperatureSensors : null,
				room.WindowSensor,
				room.PowerSensor,
				// The neighbour's own thermostat, not its area id: the action's field takes one entity id.
				NeighbourThermostat(room.Neighbour, document, resolver),
				document.Global.OutdoorSensor,
				FrostTemperature(area.Effective(document.Defaults))));
		}

		return sending;
	}

	// The integration's own version, read off any thermostat of its own that is reporting.
	private static string? VersionIn(IReadOnlyList<EntityState> states) =>
		states
			.Where(state => state.EntityId.StartsWith(WhatTheHeatingReads.ClimatePrefix, StringComparison.Ordinal))
			.Select(state => state.AttrString(HeatingActions.IntegrationVersionAttribute))
			.FirstOrDefault(version => version is { Length: > 0 });

	// The house default or the room's own; no limit is null.
	private static double? FrostTemperature(AreaSettings settings) =>
		settings.FrostProtection ? settings.FrostTemperature : null;

	private static RoomSlice Slice(
		RoomSlice? was,
		AreaConfig area,
		string roomId,
		string areaId,
		HeatingConfig document,
		IAreaRegistry registry,
		AreaEntityResolver resolver,
		PlannerDefaults defaults)
	{
		string name = AreaNaming.DisplayName(area, registry);

		// A room the resolver refuses still shows on the page, with its name and nothing it cannot answer for.
		resolver.TryResolve(area, document.Defaults, out ResolvedRoom? room, out _);

		bool noReading = room is not null && room.TemperatureSensors.Count == 0 && room.Neighbour is null;

		RoomSlice slice = was is null
			? new RoomSlice(
				roomId,
				name,
				RoomSensorSettings.Standard(defaults),
				RoomLearntNumbers.Starting(defaults))
			: was;

		slice = slice with
		{
			AreaId = areaId,
			Name = name,
			Thermostat = room?.Thermostat,
			NoReadingAtAll = noReading,
			Batteries = room is null ? null : resolver.BatteriesOf(room.TemperatureSensors),
			Sensors = slice.Sensors with { BorrowsFrom = room?.Neighbour }
		};

		// A room with nothing to measure it needs its on-or-off cells, and the away cell starts on.
		if (slice.NoReadingAtAll && slice.ModeSwitches.Count == 0)
			slice = slice with { ModeSwitches = RoomModeSwitches.Starting(Starting) };

		return slice;
	}

	private static string? NeighbourThermostat(string? neighbour, HeatingConfig document, AreaEntityResolver resolver)
	{
		if (neighbour is not { Length: > 0 })
			return null;

		return document.Areas.Any(area => string.Equals(area.AreaId, neighbour, StringComparison.Ordinal))
			? resolver.ThermostatIn(neighbour)
			: null;
	}
}
