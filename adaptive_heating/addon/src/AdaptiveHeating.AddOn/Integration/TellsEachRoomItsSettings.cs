using System.Globalization;

using Adaptive.Settings;

namespace AdaptiveHeating.AddOn.Integration;

/// <summary>One room's own settings, as they travel to that room's thermostat.</summary>
/// <param name="Thermostat">The climate entity the call targets. The action names no room: the room is the target.</param>
internal sealed record RoomRegulation(
	string Thermostat,
	TimeSpan FreshWindow,
	TimeSpan StuckWindow,
	double Spread,
	TimeSpan WindowDelay,
	IReadOnlyList<string>? Heaters,
	IReadOnlyList<string>? TemperatureSensors,
	string? WindowSensor,
	string? PowerSensor,
	string? Neighbour,
	string? OutdoorSensor,
	double? FrostTemperature = null);

/// <summary>Sends each room its own settings, and only where they have moved since the last send.</summary>
// A pass runs every minute, so sending unconditionally is an action per room per minute for nothing.
internal sealed class TellsEachRoomItsSettings
{
	private readonly Thermostats _thermostats;
	private readonly ILogger<TellsEachRoomItsSettings> _logger;

	// Keyed by the thermostat, which is the target, and holding what was sent rather than what was asked for.
	private readonly Dictionary<string, string> _sent = new(StringComparer.Ordinal);

	private string? _labelsSent;

	public TellsEachRoomItsSettings(Thermostats thermostats, ILogger<TellsEachRoomItsSettings> logger)
	{
		_thermostats = thermostats ?? throw new ArgumentNullException(nameof(thermostats));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <summary>Sends every room whose settings differ from what it was last sent.</summary>
	/// <returns>The thermostats written to, in the order they were written.</returns>
	public async Task<IReadOnlyList<string>> SendWhatChangedAsync(
		IReadOnlyList<RoomRegulation> rooms,
		CancellationToken token)
	{
		ArgumentNullException.ThrowIfNull(rooms);

		List<string> written = [];

		foreach (RoomRegulation room in rooms)
		{
			string asked = Fingerprint(room);

			if (_sent.TryGetValue(room.Thermostat, out string? already) && string.Equals(already, asked, StringComparison.Ordinal))
				continue;

			ActionOutcome outcome = await _thermostats.SendRoomSettingsAsync(room, token).ConfigureAwait(false);

			// Only a call that went through is remembered, so a lost one is sent again on the next pass.
			if (outcome is not ActionOutcome.Called)
			{
				_logger.LogDebug("{Thermostat} was not given its settings ({Outcome}); the next pass tries again.", room.Thermostat, outcome);

				continue;
			}

			_sent[room.Thermostat] = asked;
			written.Add(room.Thermostat);
		}

		return written;
	}

	/// <summary>Sends the House tab's two label ids, where they moved since the last send.</summary>
	/// <returns>Whether a call went through.</returns>
	public async Task<bool> SendLabelsAsync(GlobalConfig global, CancellationToken token)
	{
		ArgumentNullException.ThrowIfNull(global);

		string include = global.IncludeLabel ?? "";
		string exclude = global.ExcludeLabel ?? "";
		string asked = $"{include}|{exclude}";

		if (string.Equals(_labelsSent, asked, StringComparison.Ordinal))
			return false;

		if (await _thermostats.SetLabelsAsync(include, exclude, token).ConfigureAwait(false) is not ActionOutcome.Called)
			return false;

		_labelsSent = asked;

		return true;
	}

	/// <summary>Forgets what was sent, so the next pass sends every room its settings again.</summary>
	// A core restart drops whatever the thermostats were holding.
	public void TheConnectionCameBack()
	{
		_sent.Clear();
		_labelsSent = null;
	}

	private static string Fingerprint(RoomRegulation room) =>
		string.Join(
			'|',
			Number(room.FreshWindow.TotalSeconds),
			Number(room.StuckWindow.TotalSeconds),
			Number(room.Spread),
			Number(room.WindowDelay.TotalSeconds),
			string.Join(',', room.Heaters ?? []),
			string.Join(',', room.TemperatureSensors ?? []),
			room.WindowSensor ?? "",
			room.PowerSensor ?? "",
			room.Neighbour ?? "",
			room.OutdoorSensor ?? "",
			room.FrostTemperature is { } frost ? Number(frost) : "none");

	private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
