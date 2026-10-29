namespace AdaptiveHeating.Page.Settings;

/// <summary>What one room's thermostat reads and holds, as the last pass over the rooms found it.</summary>
/// <param name="RoomId">What both halves name the room by, never what a person calls it.</param>
/// <param name="Temperature">The room's own reading, or <c>null</c> where it has none.</param>
/// <param name="Target">The temperature the thermostat is holding, which is a warm-up's while one runs.</param>
/// <param name="HeatingNow">Whether the relay is closed.</param>
/// <param name="ReadAt">When the thermostat was last read, so a stale line says so instead of reading as current.</param>
public sealed record RoomNow(
	string RoomId,
	double? Temperature,
	double Target,
	bool HeatingNow,
	DateTimeOffset ReadAt);

/// <summary>What the add-on decided about one room on the last pass, beyond the temperature it sent.</summary>
/// <param name="Enabled">Whether the room is commanded at all; a disabled room is still read.</param>
/// <param name="PausedBy">The entity that has the room heated as in Away, or <c>null</c>.</param>
/// <param name="HeldBy">The entity that keeps the room at what it holds, or <c>null</c>.</param>
public sealed record RoomVerdict(bool Enabled, string? PausedBy, string? HeldBy)
{
	/// <summary>Whether the house's master switch stopped every command on that pass.</summary>
	public bool MasterSwitchOff { get; init; }

	/// <summary>The room's last change at its thermostat and who made it, kept across a restart.</summary>
	public Adaptive.Engine.AreaHistory? History { get; init; }

	/// <summary>When the add-on last sent the room a temperature.</summary>
	public DateTimeOffset? CommandedAt { get; init; }

	/// <summary>The night setback in a few words: its temperature and restart, or why there is none.</summary>
	public string? NightSetback { get; init; }

	/// <summary>The thermostat's own minimum where it is the floor tonight's setback stops at, or <c>null</c>.</summary>
	public double? SetbackThermostatFloor { get; init; }
}
