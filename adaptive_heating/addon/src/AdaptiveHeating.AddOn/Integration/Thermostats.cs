using AdaptiveLighting.Engine;

namespace AdaptiveHeating.AddOn.Integration;

/// <summary>The five actions, each with its own fields, called against one room's thermostat.</summary>
internal sealed class Thermostats
{
	private readonly CoreApi _api;
	private readonly Func<CommandLog?> _log;
	private readonly Func<DateTimeOffset> _now;

	/// <param name="log">Lighting's command log, or <c>null</c> while the document has it switched off.</param>
	/// <param name="now">The household's wall clock, which every line is stamped on.</param>
	public Thermostats(CoreApi api, Func<CommandLog?>? log = null, Func<DateTimeOffset>? now = null)
	{
		_api = api ?? throw new ArgumentNullException(nameof(api));
		_log = log ?? (() => null);
		_now = now ?? (() => DateTimeOffset.Now);
	}

	// Every command passes here, so the log holds each one with what came of it.
	private async Task<ActionOutcome> Send(
		string domain,
		string action,
		string? thermostat,
		IReadOnlyDictionary<string, object?> fields,
		CancellationToken token)
	{
		ActionOutcome outcome = await _api.CallAsync(domain, action, thermostat, fields, token).ConfigureAwait(false);

		_log()?.Write(thermostat, () => CommandLog.Line(
			_now(), $"{domain}.{action}", fields, outcome.ToString().ToLowerInvariant()));

		return outcome;
	}

	/// <summary>The room starts early enough to be at <paramref name="temperature"/> by <paramref name="byTime"/>.</summary>
	public Task<ActionOutcome> WarmRoomByAsync(string thermostat, double temperature, DateTimeOffset byTime, CancellationToken token) =>
		Send(HeatingActions.Domain, HeatingActions.WarmRoomBy, thermostat, new Dictionary<string, object?>(StringComparer.Ordinal)
		{
			[HeatingActions.Temperature] = temperature,
			[HeatingActions.ByTime] = CoreApi.AsTime(byTime)
		}, token);

	/// <summary>The room holds <paramref name="temperature"/> for <paramref name="lasts"/>, then falls back to its target.</summary>
	public Task<ActionOutcome> HoldTemperatureAsync(string thermostat, double temperature, TimeSpan lasts, CancellationToken token) =>
		Send(HeatingActions.Domain, HeatingActions.HoldTemperature, thermostat, new Dictionary<string, object?>(StringComparer.Ordinal)
		{
			[HeatingActions.Temperature] = temperature,
			[HeatingActions.Duration] = lasts.TotalSeconds
		}, token);

	/// <summary>The room works backwards from a deadline on <paramref name="degreesPerHour"/>.</summary>
	public Task<ActionOutcome> SetWarmingRateAsync(
		string thermostat,
		double degreesPerHour,
		double? measuredAtOutdoor,
		CancellationToken token) =>
		Send(HeatingActions.Domain, HeatingActions.SetWarmingRate, thermostat, new Dictionary<string, object?>(StringComparer.Ordinal)
		{
			[HeatingActions.DegreesPerHour] = degreesPerHour,
			[HeatingActions.MeasuredAtOutdoor] = measuredAtOutdoor
		}, token);

	/// <summary>The loop runs on these numbers until it is given others. Anything left out is left as it was.</summary>
	public Task<ActionOutcome> SetRegulationAsync(
		string thermostat,
		string? behaviour = null,
		double? band = null,
		double? outdoorShiftPerDegree = null,
		TimeSpan? cycleLength = null,
		double? fullOutputBelow = null,
		TimeSpan? shortestTimeBetweenRelayChanges = null,
		string? switched = null,
		CancellationToken token = default) =>
		Send(HeatingActions.Domain, HeatingActions.SetRegulation, thermostat, new Dictionary<string, object?>(StringComparer.Ordinal)
		{
			[HeatingActions.Behaviour] = behaviour,
			[HeatingActions.Band] = band,
			[HeatingActions.OutdoorShiftPerDegree] = outdoorShiftPerDegree,
			[HeatingActions.CycleLength] = cycleLength?.TotalSeconds,
			[HeatingActions.FullOutputBelow] = fullOutputBelow,
			[HeatingActions.ShortestTimeBetweenRelayChanges] = shortestTimeBetweenRelayChanges?.TotalSeconds,
			[HeatingActions.Switched] = switched
		}, token);

	/// <summary>The room's own settings, against its own thermostat. The action names no room: the room is the target.</summary>
	public Task<ActionOutcome> SendRoomSettingsAsync(RoomRegulation room, CancellationToken token)
	{
		ArgumentNullException.ThrowIfNull(room);

		return Send(HeatingActions.Domain, HeatingActions.SetRegulation, room.Thermostat, new Dictionary<string, object?>(StringComparer.Ordinal)
		{
			[HeatingActions.SensorFreshness] = room.FreshWindow.TotalSeconds,
			[HeatingActions.StuckReading] = room.StuckWindow.TotalSeconds,
			[HeatingActions.SensorSpread] = room.Spread,
			[HeatingActions.WindowDelay] = room.WindowDelay.TotalSeconds,
			[HeatingActions.Heaters] = room.Heaters,
			[HeatingActions.TemperatureSensors] = room.TemperatureSensors,
			[HeatingActions.WindowSensor] = room.WindowSensor,
			[HeatingActions.PowerSensor] = room.PowerSensor,
			[HeatingActions.Neighbour] = room.Neighbour,
			[HeatingActions.OutdoorSensor] = room.OutdoorSensor,
			// No frost limit sends the limit off and leaves the temperature as the integration holds it.
			[HeatingActions.FrostTemperature] = room.FrostTemperature,
			[HeatingActions.FrostLimit] = room.FrostTemperature is null ? HeatingActions.SwitchedOff : HeatingActions.SwitchedOn
		}, token);
	}

	/// <summary>Places the room's thermostat in another area, and carries back the sentence a refusal gave.</summary>
	public async Task<(ActionOutcome Outcome, string? Said)> MoveToAreaAsync(string thermostat, string area, CancellationToken token)
	{
		Dictionary<string, object?> fields = new(StringComparer.Ordinal) { [HeatingActions.Area] = area };

		(ActionOutcome outcome, string? said) = await _api
			.CallSayingWhyAsync(HeatingActions.Domain, HeatingActions.MoveToArea, thermostat, fields, token)
			.ConfigureAwait(false);

		_log()?.Write(thermostat, () => CommandLog.Line(
			_now(), $"{HeatingActions.Domain}.{HeatingActions.MoveToArea}", fields, outcome.ToString().ToLowerInvariant()));

		return (outcome, said);
	}

	/// <summary>The label ids the integration finds its rooms by. An empty include label finds every candidate.</summary>
	public Task<ActionOutcome> SetLabelsAsync(string includeLabel, string excludeLabel, CancellationToken token) =>
		Send(HeatingActions.Domain, HeatingActions.SetLabels, thermostat: null, new Dictionary<string, object?>(StringComparer.Ordinal)
		{
			[HeatingActions.IncludeLabel] = includeLabel,
			[HeatingActions.ExcludeLabel] = excludeLabel
		}, token);

	/// <summary>The temperature the room holds now, through Home Assistant's own climate action rather than one of ours.</summary>
	/// <remarks>Nothing clamps it. The away mode's temperature is the frost protection and not a floor, so a
	/// temperature set below it is accepted and reaches the room.</remarks>
	public Task<ActionOutcome> SetTemperatureAsync(string thermostat, double temperature, CancellationToken token) =>
		Send(HeatingActions.ClimateDomain, HeatingActions.SetTemperature, thermostat,
			new Dictionary<string, object?>(StringComparer.Ordinal) { [HeatingActions.Temperature] = temperature }, token);

	/// <summary>The preset on the room's card, through Home Assistant's own climate action.</summary>
	public Task<ActionOutcome> SetPresetAsync(string thermostat, string preset, CancellationToken token) =>
		Send(HeatingActions.ClimateDomain, HeatingActions.SetPresetMode, thermostat,
			new Dictionary<string, object?>(StringComparer.Ordinal) { [HeatingActions.PresetMode] = preset }, token);

	/// <summary>A room with no reading takes on or off in a mode's place, and this is how it arrives.</summary>
	public Task<ActionOutcome> SwitchAsync(string thermostat, bool on, CancellationToken token) =>
		SetRegulationAsync(thermostat, switched: on ? HeatingActions.SwitchedOn : HeatingActions.SwitchedOff, token: token);

	/// <summary>Any warm-up and any timed hold end, and the room holds the temperature it was last set.</summary>
	public Task<ActionOutcome> ReturnToTargetAsync(string thermostat, CancellationToken token) =>
		Send(HeatingActions.Domain, HeatingActions.ReturnToTarget, thermostat,
			new Dictionary<string, object?>(StringComparer.Ordinal), token);
}
