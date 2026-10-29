namespace AdaptiveHeating.AddOn.Integration;

/// <summary>Which of Home Assistant's entities the heating reads at all.</summary>
/// <remarks>
///     One predicate does two jobs: it decides what is kept as the house changes, and it decides which change is
///     worth a pass over the rooms. A house raises hundreds of state changes a minute and the heating reads a
///     handful of entities, so filtering here is what keeps the rooms from being driven on somebody else's
///     doorbell.
/// </remarks>
internal static class WhatTheHeatingReads
{
	/// <summary>Home Assistant's own sun entity, which is what a sun-anchored period start is placed against.</summary>
	public const string Sun = "sun.sun";

	/// <summary>Every thermostat of this integration is a climate entity, and the add-on finds its rooms among them.</summary>
	public const string ClimatePrefix = "climate.";

	/// <summary>
	/// Every calendar in the house, because the settings page offers them and a chosen one has to be seen to be
	/// there at all.
	/// </summary>
	// The arrivals themselves are read over a window and never off the state, which names only the nearest event.
	public const string CalendarPrefix = "calendar.";

	/// <summary>
	/// Whether the heating reads <paramref name="entityId"/>. <paramref name="alsoNamed"/> holds what the add-on's
	/// own configuration points at: the presence helper, the chosen outdoor sensors and the forecast behind them.
	/// </summary>
	public static bool Reads(string entityId, IReadOnlyCollection<string> alsoNamed)
	{
		ArgumentNullException.ThrowIfNull(entityId);
		ArgumentNullException.ThrowIfNull(alsoNamed);

		return entityId.StartsWith(ClimatePrefix, StringComparison.Ordinal)
			|| entityId.StartsWith(CalendarPrefix, StringComparison.Ordinal)
			|| string.Equals(entityId, Sun, StringComparison.Ordinal)
			|| alsoNamed.Contains(entityId);
	}

	/// <summary>Whether one change off the entity model's own stream is worth a pass over the rooms.</summary>
	public static bool Reads(StateChange change, IReadOnlyCollection<string> alsoNamed)
	{
		ArgumentNullException.ThrowIfNull(change);
		ArgumentNullException.ThrowIfNull(alsoNamed);

		return change.EntityId() is { Length: > 0 } entityId && Reads(entityId, alsoNamed);
	}
}
