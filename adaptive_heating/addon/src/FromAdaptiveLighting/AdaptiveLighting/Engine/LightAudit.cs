namespace AdaptiveLighting.Engine;

/// <summary>One heater as the audit reads it.</summary>
public sealed record LightUnderReview(string EntityId, string Name);

/// <summary>One heater the audit flags, and why, in a person's words.</summary>
public sealed record SuspectLight(string EntityId, string Name, string Reason);

/// <summary>One room as the cross-room half of the audit reads it.</summary>
// Groups already followed down to their members: two groups over one heater share no id.
public sealed record RoomUnderReview(string Room, IReadOnlyList<LightUnderReview> Lights);

/// <summary>Looks over the heaters the rooms would command and points at the ones two rooms both command.</summary>
public static class LightAudit
{
	/// <summary>The heaters more than one room will command, one entry per heater.</summary>
	// hasOwnArea is asked only after the cheap test fails, because answering it sweeps the registry.
	public static IReadOnlyList<SuspectLight> SharedBetweenRooms(
		IReadOnlyList<RoomUnderReview> rooms,
		Func<string, bool> hasOwnArea)
	{
		ArgumentNullException.ThrowIfNull(rooms);
		ArgumentNullException.ThrowIfNull(hasOwnArea);

		Dictionary<string, (LightUnderReview Light, List<string> Rooms)> commanded = new(StringComparer.Ordinal);
		List<string> order = [];

		foreach (RoomUnderReview room in rooms)
			foreach (LightUnderReview light in room.Lights)
			{
				if (!commanded.TryGetValue(light.EntityId, out (LightUnderReview Light, List<string> Rooms) seen))
				{
					commanded[light.EntityId] = seen = (light, []);
					order.Add(light.EntityId);
				}

				// One room holding the same heater twice, through a group and on its own, is one room.
				if (!seen.Rooms.Contains(room.Room, StringComparer.Ordinal))
					seen.Rooms.Add(room.Room);
			}

		List<SuspectLight> shared = [];

		foreach (string entityId in order)
		{
			(LightUnderReview light, List<string> claimants) = commanded[entityId];

			if (claimants.Count < 2 || hasOwnArea(entityId))
				continue;

			shared.Add(new SuspectLight(entityId, light.Name, SharedReason(claimants)));
		}

		return shared;
	}

	// Read as the second half of a line that starts with the heater's own name.
	private static string SharedReason(IReadOnlyList<string> rooms) =>
		$"commanded by {Join(rooms)} at once, because more than one room names it; "
		+ "name it in one room only and only that room will set its temperature";

	private static string Join(IReadOnlyList<string> names) => names.Count switch
	{
		1 => names[0],
		2 => $"{names[0]} and {names[1]}",
		_ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}"
	};
}
