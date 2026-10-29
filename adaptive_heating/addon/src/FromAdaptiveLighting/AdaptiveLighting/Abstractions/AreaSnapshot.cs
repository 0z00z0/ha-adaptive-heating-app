using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Abstractions;

/// <summary>Everything worth knowing about a room at one instant.</summary>
// Unknown is null, never a default. AreaId is the join back to the document; AreaName can change mid-session.
public sealed record AreaSnapshot(
	string AreaName,
	AreaState State,
	TransitionReason Reason,
	ModeKind Mode,
	bool KillSwitchActive,
	string? PeriodName,
	double? Target,
	double? Temperature,
	DateTimeOffset Timestamp,
	DateTimeOffset? LastCommandAt,
	DateTimeOffset? NextChangeAt,
	DateTimeOffset? NextChangeFrom,
	string? HouseModeValue = null,
	string? AreaId = null,
	AutoOnBlock? AutoOnBlockedBy = null,
	string? AutoOnBlockingEntity = null,
	bool? LevelsFromRoom = null,
	bool? IsAnyoneHome = null,
	ForcedMode? Forced = null,
	bool? IsHeld = null,
	string? HeldBy = null,
	string? TargetFrom = null,
	int? HeatersNotResponding = null,
	int? HeaterCount = null,
	string? ChangedBy = null,
	DateTimeOffset? ChangedAt = null,
	IReadOnlyList<SensorBattery>? LowBatteries = null,
	string? ActiveModeName = null,
	string? RoomId = null,
	string? PeriodId = null,
	string? ChangedByUserId = null)
{
	/// <summary>Whether <paramref name="other"/> carries the same news about the room as this snapshot does.</summary>
	// Not record equality: the timestamps move on every pass, and diffing on them would suppress nothing.
	public bool HasSameMeaningAs(AreaSnapshot? other) =>
		other is not null &&
		State == other.State &&
		Mode == other.Mode &&
		string.Equals(HouseModeValue, other.HouseModeValue, StringComparison.Ordinal) &&
		KillSwitchActive == other.KillSwitchActive &&
		string.Equals(PeriodName, other.PeriodName, StringComparison.Ordinal) &&
		Nullable.Equals(Target, other.Target) &&
		Nullable.Equals(NextChangeAt, other.NextChangeAt) &&
		Nullable.Equals(NextChangeFrom, other.NextChangeFrom) &&
		Nullable.Equals(LevelsFromRoom, other.LevelsFromRoom) &&
		Nullable.Equals(IsAnyoneHome, other.IsAnyoneHome) &&
		Nullable.Equals(IsHeld, other.IsHeld) &&
		HeatersNotResponding == other.HeatersNotResponding &&
		HeaterCount == other.HeaterCount &&
		SameBatteries(LowBatteries, other.LowBatteries) &&
		Forced == other.Forced;

	private static bool SameBatteries(IReadOnlyList<SensorBattery>? left, IReadOnlyList<SensorBattery>? right) =>
		(left ?? []).SequenceEqual(right ?? []);
}
