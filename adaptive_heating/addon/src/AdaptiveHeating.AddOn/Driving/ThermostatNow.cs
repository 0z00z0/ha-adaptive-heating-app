using System.Globalization;
using System.Text.Json;

using AdaptiveHeating.AddOn.Integration;

using NetDaemon.HassModel;

namespace AdaptiveHeating.AddOn.Driving;

/// <summary>What one thermostat says about its room at this moment.</summary>
// Target is the warm-up's or the hold's while one runs, and null where the thermostat publishes none.
internal sealed record ThermostatNow(
	string RoomId,
	string EntityId,
	double? RoomTemperature,
	double? Target,
	bool HeaterCommandedOn,
	bool NoReadingAtAll,
	double? Outdoor,
	DateTimeOffset? TargetSetAt,
	bool WarmUpRunning,
	DateTimeOffset? HoldEndsAt,
	double? PowerWatts,
	bool HasAPowerMeter,
	Context? Context)
{
	/// <summary>The preset held on the room's card, or <c>null</c> where it holds none.</summary>
	public string? Preset { get; init; }

	/// <summary>When that preset was chosen, which a hand change is weighed against.</summary>
	public DateTimeOffset? PresetSetAt { get; init; }

	/// <summary>Whether the thermostat's features include presets; one without them is never written one.</summary>
	public bool TakesAPreset { get; init; }

	/// <summary>The integration reports a meter fault, a sensor left out, no reading or a heater not answering.</summary>
	public bool ReportsAFault { get; init; }

	/// <summary>How many heaters the room drives, as the integration counts them.</summary>
	public int? HeaterCount { get; init; }

	/// <summary>How many of them did not answer at the integration's last evaluation.</summary>
	public int? HeatersNotResponding { get; init; }

	/// <summary>The lowest the room's own thermostat or one underneath it accepts, or <c>null</c> where neither says.</summary>
	public double? ThermostatMinimum { get; init; }

	/// <summary>Reads one climate entity, or <c>null</c> where it is not one of this integration's thermostats.</summary>
	public static ThermostatNow? From(EntityState state, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(state);

		if (state.AttrString(HeatingActions.IntegrationVersionAttribute) is not { Length: > 0 })
			return null;

		if (state.AttrString(HeatingActions.RoomIdAttribute) is not { Length: > 0 } roomId)
			return null;

		(DateTimeOffset? starts, DateTimeOffset? deadline) = WarmUpWindow(state);

		// Not refused for a missing target. That is a room with no reading, which publishes none and takes on or
		// off in a mode's place; refusing it here takes the room out of the pass and the on-or-off path with it.
		return new ThermostatNow(
			roomId,
			state.EntityId,
			state.AttrDouble(HeatingActions.CurrentTemperatureAttribute),
			state.AttrDouble(HeatingActions.TargetTemperatureAttribute),
			string.Equals(state.AttrString(HeatingActions.HvacActionAttribute), HeatingActions.Heating, StringComparison.Ordinal),
			Attributes.Flag(state, HeatingActions.NoReadingAtAllAttribute) ?? false,
			state.AttrDouble(HeatingActions.OutdoorAttribute),
			state.AttrDateTimeOffset(HeatingActions.TargetSetAtAttribute),
			starts <= now && now < deadline,
			state.AttrDateTimeOffset(HeatingActions.HoldEndsAtAttribute),
			state.AttrDouble(HeatingActions.PowerAttribute),
			Attributes.Flag(state, HeatingActions.HasAPowerMeterAttribute) ?? false,
			state.Context)
		{
			Preset = state.AttrString(HeatingActions.PresetModeAttribute) is { Length: > 0 } preset ? preset : null,
			PresetSetAt = state.AttrDateTimeOffset(HeatingActions.PresetSetAtAttribute),
			TakesAPreset = state.AttrDouble(HeatingActions.SupportedFeaturesAttribute) is { } features
				&& ((long)features & HeatingActions.PresetModeFeature) != 0,
			ReportsAFault = state.AttrString(HeatingActions.FaultAttribute) is { Length: > 0 }
				|| Attributes.Flag(state, HeatingActions.ReportsAProblemAttribute) is true,
			HeaterCount = (int?)state.AttrDouble(HeatingActions.HeaterCountAttribute),
			HeatersNotResponding = (int?)state.AttrDouble(HeatingActions.HeatersNotRespondingAttribute),
			ThermostatMinimum = Highest(
				state.AttrDouble(HeatingActions.ThermostatMinimumAttribute),
				state.AttrDouble(HeatingActions.MinTempAttribute))
		};
	}

	public bool HoldUnexpiredAt(DateTimeOffset now) => HoldEndsAt > now;

	private static double? Highest(double? one, double? other) =>
		one is { } a && other is { } b ? Math.Max(a, b) : one ?? other;

	private static (DateTimeOffset? Starts, DateTimeOffset? Deadline) WarmUpWindow(EntityState state) =>
		(Attributes.MomentIn(state, HeatingActions.WarmUpAttribute, "starts_at"),
			Attributes.MomentIn(state, HeatingActions.WarmUpAttribute, "deadline"));
}

/// <summary>The two attribute shapes the copied readers do not cover: a flag, and a moment inside an object.</summary>
internal static class Attributes
{
	public static bool? Flag(EntityState? state, string name) =>
		Value(state, name) switch
		{
			JsonElement { ValueKind: JsonValueKind.True } => true,
			JsonElement { ValueKind: JsonValueKind.False } => false,
			bool flag => flag,
			_ => null
		};

	public static DateTimeOffset? MomentIn(EntityState? state, string holder, string name) =>
		Value(state, holder) is JsonElement { ValueKind: JsonValueKind.Object } element
		&& element.TryGetProperty(name, out JsonElement value)
		&& value.ValueKind == JsonValueKind.String
		&& DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset moment)
			? moment
			: null;

	private static object? Value(EntityState? state, string name) =>
		state?.Attributes?.TryGetValue(name, out object? value) == true ? value : null;
}
