using Adaptive.Settings;

namespace AdaptiveHeating.AddOn.Settings;

/// <summary>The outdoor sensors a person chose, and the forecast that stands in when every one of them fails.</summary>
/// <param name="StaleAfterMinutes">How long a sensor may stay silent before it is left out; zero or less never.</param>
internal sealed record OutdoorChoice(IReadOnlyList<string> Sensors, string? Forecast, int StaleAfterMinutes = 120)
{
	public static OutdoorChoice Nothing { get; } = new([], null);

	public bool Chosen => Sensors.Count > 0 || Forecast is { Length: > 0 };

	/// <summary>Every entity this choice names, which is what the connection has to be watching.</summary>
	public IEnumerable<string> Entities =>
		Forecast is { Length: > 0 } forecast ? [.. Sensors, forecast] : Sensors;

	/// <summary>The choice the document holds. The house names one outdoor sensor, so every room gets the same one.</summary>
	public static OutdoorChoice From(GlobalConfig global)
	{
		ArgumentNullException.ThrowIfNull(global);

		string[] sensors = global.OutdoorSensor is { Length: > 0 } named ? [named] : [];

		return sensors.Length == 0 && global.ForecastEntity is not { Length: > 0 }
			? Nothing
			: new OutdoorChoice(sensors, global.ForecastEntity, global.OutdoorSensorStaleAfterMinutes);
	}
}
