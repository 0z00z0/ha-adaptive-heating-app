namespace AdaptiveHeating.Page.Settings;

/// <summary>One calendar Home Assistant holds, as the settings page offers it.</summary>
/// <param name="EntityId">What the calendar is chosen and looked up by. A rename never moves it.</param>
/// <param name="Name">What a person reads beside it. Shown and nothing else.</param>
/// <param name="Local">Whether Home Assistant itself keeps the calendar, which the record is written to and no other.</param>
public sealed record CalendarOnOffer(string EntityId, string Name, bool Local);
