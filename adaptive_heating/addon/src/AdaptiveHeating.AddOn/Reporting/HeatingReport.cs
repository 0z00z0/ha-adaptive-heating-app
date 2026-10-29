namespace AdaptiveHeating.AddOn.Reporting;

/// <summary>What one report is about.</summary>
// Numbered: the journal file keeps these by number, so a removed member leaves a gap and a new one takes the next.
internal enum WhatHappened
{
	/// <summary>The add-on started, which is the first line of every run.</summary>
	Started = 0,

	/// <summary>A mode came into force over the whole house.</summary>
	ModeInForce = 1,

	/// <summary>A room was told the temperature to hold.</summary>
	RoomHolds = 2,

	/// <summary>A room with no reading was switched on or off in a mode's place.</summary>
	RoomSwitched = 3,

	/// <summary>A room began heating early, to be at a temperature by a deadline.</summary>
	WarmUpStarted = 4,

	/// <summary>A room cannot reach the temperature by the deadline, however early it starts.</summary>
	CannotWarmInTime = 5,

	/// <summary>A room's warming rate was measured again.</summary>
	WarmingRateLearnt = 6,

	/// <summary>A room rose with its heater idle, so nothing was learnt from the rise.</summary>
	RoseWithoutHeating = 7,

	/// <summary>A room's outdoor term was fitted again from how far below its target it settled.</summary>
	OutdoorTermLearnt = 8,

	/// <summary>Every chosen outdoor sensor failed the check.</summary>
	OutdoorSensorsAllFailed = 9,

	/// <summary>The calendar the arrivals are read from is named in the settings and is not in Home Assistant.</summary>
	ArrivalCalendarMissing = 11,

	/// <summary>A finished warm-up was written into the record calendar.</summary>
	WarmUpRecorded = 12,

	/// <summary>A warm-up finished and there was nowhere to write it. Said once, not once a minute.</summary>
	WarmUpNotRecorded = 13,

	/// <summary>The household's zone did not resolve, so every wall time is wrong until it is put right.</summary>
	HouseholdZoneNotResolved = 14,

	/// <summary>A save was taken up, and every room follows it from the next pass.</summary>
	SettingsSaved = 15,

	/// <summary>Somebody or another automation set a room's temperature at its thermostat.</summary>
	HandChange = 16,

	/// <summary>A change at the thermostat held for the room's hold length, and the mode took the room back.</summary>
	HandChangeEnded = 17,

	/// <summary>A room followed its thermostat to another area in Home Assistant. Named holds the area it left.</summary>
	RoomMoved = 18
}

/// <summary>One thing the add-on did, or could not do. A line of the activity record and of the durable log.</summary>
/// <remarks>Kept in the journal file, so a field added here is read back as absent by an older file.</remarks>
internal sealed record HeatingReport
{
	public required DateTimeOffset At { get; init; }

	public required WhatHappened What { get; init; }

	/// <summary>The room, by the id both halves name it by, or <c>null</c> where the report is house-wide.</summary>
	// Never the name a person reads: a rename would split one room's history in two.
	public string? RoomId { get; init; }

	/// <summary>The temperature the report is about: the one held, the one wanted, or the one asked for.</summary>
	public double? Temperature { get; init; }

	/// <summary>The temperature the first is weighed against: the one a room reaches, or the outdoor reading.</summary>
	public double? Against { get; init; }

	/// <summary>Degrees an hour, where the report is about how fast a room warms.</summary>
	public double? DegreesPerHour { get; init; }

	/// <summary>Whether a room with no reading was switched on.</summary>
	public bool? SwitchedOn { get; init; }

	/// <summary>What the report names: a mode, what chose a temperature, or an entity.</summary>
	public string? Named { get; init; }

	/// <summary>The Home Assistant user behind a change by a person. The person is named when the line is read.</summary>
	public string? ChangedByUserId { get; init; }

	/// <summary>The automation behind a change by an automation, by entity id. It is named when the line is read.</summary>
	public string? ChangedByAutomationId { get; init; }

	/// <summary>The deadline a warm-up works back from.</summary>
	public DateTimeOffset? Deadline { get; init; }

	/// <summary>Whether this one is worth a card in Home Assistant rather than only a line in the record.</summary>
	/// <remarks>Only what the add-on cannot put right itself. Everything else would train a person to dismiss cards.</remarks>
	public bool NeedsAPerson =>
		What is WhatHappened.CannotWarmInTime
			or WhatHappened.OutdoorSensorsAllFailed
			or WhatHappened.ArrivalCalendarMissing
			or WhatHappened.WarmUpNotRecorded
			or WhatHappened.HouseholdZoneNotResolved;

	public static HeatingReport About(WhatHappened what, DateTimeOffset at, string? roomId = null) =>
		new() { At = at, What = what, RoomId = roomId };
}
