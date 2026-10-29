using System.Globalization;
using System.Text.Json;

using Adaptive.Settings;

using AdaptiveHeating.AddOn.Integration;
using AdaptiveHeating.AddOn.Settings;
using AdaptiveHeating.Planner;

namespace AdaptiveHeating.AddOn.Driving;

/// <summary>Where the outdoor temperature the rooms are running on came from.</summary>
internal enum OutdoorFrom
{
	/// <summary>Nothing was chosen, so each thermostat's own attribute is all there is.</summary>
	NothingChosen,

	/// <summary>One or more of the chosen sensors passed the check.</summary>
	Sensors,

	/// <summary>Every chosen sensor failed the check, so the weather entity's own reading is what the rooms run on.</summary>
	Forecast,

	/// <summary>Every chosen sensor failed the check, so the hours the forecast names ahead are what the rooms run on.</summary>
	ForecastAhead,

	/// <summary>Sensors were chosen, and neither they nor the forecast answered.</summary>
	Nothing
}

/// <summary>The outdoor temperature this pass, and what the check made of each chosen sensor.</summary>
internal sealed record OutdoorReading(double? Temperature, OutdoorFrom From, RoomReading Sensors);

/// <summary>The outdoor temperature, from the sensors a person chose and from the forecast behind them.</summary>
// The sensors go through the same check a room's own do, in RoomSensors.Read.
internal static class TheOutdoorTemperature
{
	/// <summary>The attribute a weather entity carries its own current temperature in.</summary>
	public const string ForecastAttribute = "temperature";

	public const string WeatherDomain = "weather";

	public const string GetForecasts = "get_forecasts";

	public const string Hourly = "hourly";

	private const string ForecastField = "forecast";
	private const string MomentField = "datetime";

	/// <summary>How far ahead a warm-up can be planning, which bounds the hours the forecast is reduced over.</summary>
	public static readonly TimeSpan HoursAheadThatMatter = TimeSpan.FromHours(12);

	private static readonly RoomReading NoneChosen = new(null, 0, 0, []);

	public static OutdoorReading Read(
		IReadOnlyList<EntityState> states,
		OutdoorChoice chosen,
		DateTimeOffset asAt,
		PlannerDefaults defaults,
		IEntityLastSeen? lastSeen = null)
	{
		RoomReading reading = FromTheSensors(states, chosen, asAt, defaults, lastSeen, out OutdoorReading? settled);

		if (settled is not null)
			return settled;

		if (chosen.Forecast is { Length: > 0 } forecast
			&& Find(states, forecast).AttrDouble(ForecastAttribute) is { } fromTheForecast)
		{
			return new OutdoorReading(fromTheForecast, OutdoorFrom.Forecast, reading);
		}

		return new OutdoorReading(null, OutdoorFrom.Nothing, reading);
	}

	/// <summary>The same reading, asking the forecast for the hours ahead where that is the source named.</summary>
	public static async Task<OutdoorReading> ReadAsync(
		IReadOnlyList<EntityState> states,
		OutdoorChoice chosen,
		WarmUpSource source,
		CoreApi api,
		DateTimeOffset asAt,
		PlannerDefaults defaults,
		CancellationToken token,
		IEntityLastSeen? lastSeen = null)
	{
		ArgumentNullException.ThrowIfNull(api);

		if (source == WarmUpSource.OutdoorMeasurement || chosen.Forecast is not { Length: > 0 } forecast)
			return Read(states, chosen, asAt, defaults, lastSeen);

		RoomReading reading = FromTheSensors(states, chosen, asAt, defaults, lastSeen, out OutdoorReading? settled);

		if (settled is not null)
			return settled;

		(ActionOutcome outcome, JsonElement? answer) = await api
			.CallAsync(
				WeatherDomain,
				GetForecasts,
				forecast,
				new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = Hourly },
				wantsAnAnswer: true,
				token)
			.ConfigureAwait(false);

		if (outcome == ActionOutcome.Called
			&& answer is { } said
			&& HoursAhead(said, forecast, asAt, HoursAheadThatMatter) is { } ahead)
		{
			return new OutdoorReading(ahead, OutdoorFrom.ForecastAhead, reading);
		}

		// The weather entity's own reading stands in where the call did not answer, so a house keeps a number.
		return Read(states, chosen, asAt, defaults, lastSeen);
	}

	/// <summary>The one number the hours ahead reduce to: the lowest, so a warm-up planned on it is never late.</summary>
	public static double? HoursAhead(JsonElement answer, string entityId, DateTimeOffset asAt, TimeSpan within)
	{
		if (answer.ValueKind != JsonValueKind.Object
			|| !answer.TryGetProperty(entityId, out JsonElement forEntity)
			|| forEntity.ValueKind != JsonValueKind.Object
			|| !forEntity.TryGetProperty(ForecastField, out JsonElement hours)
			|| hours.ValueKind != JsonValueKind.Array)
		{
			return null;
		}

		DateTimeOffset until = asAt + within;
		double? lowest = null;

		foreach (JsonElement hour in hours.EnumerateArray())
		{
			if (hour.ValueKind != JsonValueKind.Object
				|| !hour.TryGetProperty(ForecastAttribute, out JsonElement value)
				|| value.ValueKind != JsonValueKind.Number)
			{
				continue;
			}

			// An hour with no readable moment is kept: the whole answer is hours ahead of now to begin with.
			if (Moment(hour) is { } at && (at < asAt || at > until))
				continue;

			double reading = value.GetDouble();

			lowest = lowest is { } sofar ? Math.Min(sofar, reading) : reading;
		}

		return lowest;
	}

	private static RoomReading FromTheSensors(
		IReadOnlyList<EntityState> states,
		OutdoorChoice chosen,
		DateTimeOffset asAt,
		PlannerDefaults defaults,
		IEntityLastSeen? lastSeen,
		out OutdoorReading? settled)
	{
		ArgumentNullException.ThrowIfNull(states);
		ArgumentNullException.ThrowIfNull(chosen);
		ArgumentNullException.ThrowIfNull(defaults);

		if (!chosen.Chosen)
		{
			settled = new OutdoorReading(null, OutdoorFrom.NothingChosen, NoneChosen);

			return NoneChosen;
		}

		// The house's own staleness rule, as lighting applies its outdoor one. Zero or less switches it off.
		int staleAfter = chosen.StaleAfterMinutes;
		PlannerDefaults outdoor = defaults with
		{
			SensorFreshness = staleAfter > 0 ? TimeSpan.FromMinutes(staleAfter) : TimeSpan.MaxValue
		};

		List<SensorReading> reporting = [];

		foreach (string sensorId in chosen.Sensors)
		{
			if (Find(states, sensorId) is not { } sensor || sensor.StateAsDouble() is not { } temperature)
				continue;

			reporting.Add(new SensorReading(
				sensorId,
				temperature,
				// The cache outranks Home Assistant's own stamp, which a core restart resets for every entity.
				lastSeen?.LastSeenUtc(sensorId) ?? Moment(sensor.LastUpdated) ?? asAt,
				Moment(sensor.LastChanged) ?? asAt));
		}

		RoomReading reading = RoomSensors.Read(reporting, asAt, outdoor);

		settled = reading.Temperature is { } fromTheSensors
			? new OutdoorReading(fromTheSensors, OutdoorFrom.Sensors, reading)
			: null;

		return reading;
	}

	private static EntityState? Find(IReadOnlyList<EntityState> states, string entityId) =>
		states.FirstOrDefault(state => string.Equals(state.EntityId, entityId, StringComparison.Ordinal));

	// The model hands over a DateTime, so the zone is named here: an unspecified kind is Home Assistant's own UTC.
	private static DateTimeOffset? Moment(DateTime? value) =>
		value is { } at
			? new DateTimeOffset(at.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : at)
			: null;

	private static DateTimeOffset? Moment(JsonElement hour) =>
		hour.TryGetProperty(MomentField, out JsonElement value)
		&& value.ValueKind == JsonValueKind.String
		&& DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset at)
			? at
			: null;
}
