using AdaptiveHeating.Page.Settings;

namespace AdaptiveHeating.AddOn.Integration;

/// <summary>The calendars the settings page offers, out of what Home Assistant reports and who created each one.</summary>
internal static class CalendarsInTheHouse
{
	/// <summary>Every calendar entity in the house, in the order a person reads them.</summary>
	/// <param name="platforms">Which integration created each calendar. An entity missing from it is not local.</param>
	public static IReadOnlyList<CalendarOnOffer> Offered(
		IReadOnlyList<EntityState> states,
		IReadOnlyDictionary<string, string> platforms)
	{
		ArgumentNullException.ThrowIfNull(states);
		ArgumentNullException.ThrowIfNull(platforms);

		List<CalendarOnOffer> offered = [];

		foreach (EntityState state in states)
		{
			if (!state.EntityId.StartsWith(WhatTheHeatingReads.CalendarPrefix, StringComparison.Ordinal))
				continue;

			string name = state.AttrString("friendly_name") is { Length: > 0 } called ? called : state.EntityId;

			bool local = platforms.TryGetValue(state.EntityId, out string? platform)
				&& string.Equals(platform, CalendarActions.LocalPlatform, StringComparison.Ordinal);

			offered.Add(new CalendarOnOffer(state.EntityId, name, local));
		}

		offered.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase));

		return offered;
	}

	/// <summary>Whether a chosen calendar is among the entities Home Assistant reports.</summary>
	public static bool IsThere(IReadOnlyList<EntityState> states, string? entityId)
	{
		ArgumentNullException.ThrowIfNull(states);

		if (entityId is not { Length: > 0 })
			return false;

		foreach (EntityState state in states)
		{
			if (string.Equals(state.EntityId, entityId, StringComparison.Ordinal))
				return true;
		}

		return false;
	}
}
