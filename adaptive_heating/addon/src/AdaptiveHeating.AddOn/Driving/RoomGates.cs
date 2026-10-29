using Adaptive.Settings;

using AdaptiveHeating.Page.Settings;

namespace AdaptiveHeating.AddOn.Driving;

/// <summary>One room's own gates on this pass: enabled, paused or held while something is on, and who counts as a hand.</summary>
internal sealed record RoomGates(bool Enabled, string? PausedBy, string? HeldBy, bool AutomationsCountAsHand, TimeSpan HandHoldsFor)
{
	public RoomVerdict Verdict => new(Enabled, PausedBy, HeldBy);

	public static RoomGates Of(AreaConfig? area, HeatingConfig document, IReadOnlyDictionary<string, EntityState> states)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(states);

		if (area is null)
			return new RoomGates(
				true, null, null, document.Global.TreatAutomationsAsManual, Minutes(document.Defaults.OverrideDurationMinutes));

		AreaSettings effective = area.Effective(document.Defaults);

		return new RoomGates(
			effective.Enabled,
			FirstThatApplies(area.PauseWhenOn, area.PauseWhenOnInverted is true, states),
			FirstThatApplies(area.HoldWhenOn, area.HoldWhenOnInverted is true, states),
			area.TreatAutomationsAsManual ?? document.Global.TreatAutomationsAsManual,
			Minutes(effective.OverrideDurationMinutes));
	}

	private static TimeSpan Minutes(int minutes) => TimeSpan.FromMinutes(minutes);

	// Lighting's rule: unavailable reads as neither on nor off, so a gate that has dropped out never applies.
	private static string? FirstThatApplies(
		IReadOnlyList<string>? entities,
		bool inverted,
		IReadOnlyDictionary<string, EntityState> states) =>
		entities?.FirstOrDefault(entityId =>
			states.TryGetValue(entityId, out EntityState? state) && (inverted ? state.IsOff() : state.IsOn()));
}
