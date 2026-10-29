namespace AdaptiveLighting.Engine;

/// <summary>What a heated room is doing, as its event names it.</summary>
public enum AreaState
{
	Disabled,

	/// <summary>The house is away, or something the room watches pauses it.</summary>
	Away,

	AutoActive,

	/// <summary>A change at the thermostat stands until the room's hold length runs out.</summary>
	OverriddenOn,

	/// <summary>A guest mode, or an entity the room watches, holds the room as it stands.</summary>
	SceneHold,

	WarmingUp,

	NotReporting
}

/// <summary>Why a room's event was published.</summary>
public enum TransitionReason
{
	Startup,

	ManualOn,

	OverrideExpired,

	EnablementChanged,

	CircadianTick,

	HouseModeChanged,

	SceneHold,

	SensorBattery
}

/// <summary>Which gate stops the room following its mode and schedule.</summary>
public enum AutoOnBlock
{
	None,

	Disabled,

	KillSwitch,

	SceneHold,

	EntityOn
}
