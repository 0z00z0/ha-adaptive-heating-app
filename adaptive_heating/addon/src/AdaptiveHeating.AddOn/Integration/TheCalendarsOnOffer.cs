using AdaptiveHeating.Page.Settings;

namespace AdaptiveHeating.AddOn.Integration;

/// <summary>The calendars the settings page offers, and which integration created each one.</summary>
// Every calendar declares the same capabilities, so only the registry can say which one Home Assistant keeps.
internal sealed class TheCalendarsOnOffer
{
	private readonly Func<string, RegistryAnswer> _about;

	public TheCalendarsOnOffer(Func<string, RegistryAnswer> about) =>
		_about = about ?? throw new ArgumentNullException(nameof(about));

	/// <summary>Every calendar in the house, each marked with whether Home Assistant keeps it itself.</summary>
	public IReadOnlyList<CalendarOnOffer> Offered(IReadOnlyList<EntityState> states)
	{
		ArgumentNullException.ThrowIfNull(states);

		Dictionary<string, string> platforms = new(StringComparer.Ordinal);

		foreach (EntityState state in states)
		{
			if (!state.EntityId.StartsWith(WhatTheHeatingReads.CalendarPrefix, StringComparison.Ordinal))
				continue;

			if (_about(state.EntityId).Platform is { Length: > 0 } platform)
				platforms[state.EntityId] = platform;
		}

		return CalendarsInTheHouse.Offered(states, platforms);
	}
}
