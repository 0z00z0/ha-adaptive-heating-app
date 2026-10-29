namespace AdaptiveHeating.Planner;

/// <summary>
/// What a mode is worth in one room: a temperature, or on or off where the room has no reading at all.
/// </summary>
/// <remarks>A mode is a name, and the number or the switch beside it is typed on the settings page. Nothing
/// here holds a table of fixed temperatures.</remarks>
public readonly record struct RoomInstruction
{
	private RoomInstruction(bool holdsATemperature, double temperature, bool switchedOn)
	{
		HoldsATemperature = holdsATemperature;
		Temperature = temperature;
		SwitchedOn = switchedOn;
	}

	/// <summary>True where the room is given a temperature, false where it is given on or off.</summary>
	public bool HoldsATemperature { get; }

	public double Temperature { get; }

	public bool SwitchedOn { get; }

	public static RoomInstruction Holds(double temperature) => new(true, temperature, false);

	public static RoomInstruction Switched(bool on) => new(false, 0, on);
}

/// <summary>One room's on-or-off cell for each mode, which is what a room with no reading carries.</summary>
public static class RoomModeSwitches
{
	/// <summary>
	/// What a room with no reading starts on. The away cell starts on, because the heater's own dial is the only
	/// frost protection such a room has and a cabin left with the relay open has none at all.
	/// </summary>
	public static IReadOnlyDictionary<HeatingMode, Stamped<bool>> Starting(Stamp stamp)
	{
		Dictionary<HeatingMode, Stamped<bool>> switches = [];

		foreach (HeatingMode mode in HeatingModes.All)
			switches[mode] = new Stamped<bool>(mode == HeatingMode.Away, stamp, ChangeOrigin.SettingsPage);

		return switches;
	}
}

/// <summary>What the mode in force asks of one room.</summary>
public static class ModeTable
{
	/// <summary>
	/// The instruction this mode carries for one room, or <c>null</c> where the room has nothing against it. A room
	/// the integration reported for the first time is that case, and nothing here can invent a number for a house.
	/// </summary>
	public static Stamped<RoomInstruction>? For(
		HeatingMode mode,
		bool roomHasAReading,
		IReadOnlyDictionary<HeatingMode, Stamped<double>> temperatures,
		IReadOnlyDictionary<HeatingMode, Stamped<bool>> switches)
	{
		ArgumentNullException.ThrowIfNull(temperatures);
		ArgumentNullException.ThrowIfNull(switches);

		if (!roomHasAReading)
		{
			return switches.TryGetValue(mode, out Stamped<bool> on)
				? new Stamped<RoomInstruction>(RoomInstruction.Switched(on.Value), on.Stamp, on.Origin)
				: null;
		}

		return temperatures.TryGetValue(mode, out Stamped<double> held)
			? new Stamped<RoomInstruction>(RoomInstruction.Holds(held.Value), held.Stamp, held.Origin)
			: null;
	}
}
