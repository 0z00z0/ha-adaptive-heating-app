namespace AdaptiveLighting.Abstractions;

/// <summary>What the engine did to itself, as opposed to something an area reported.</summary>
public enum EngineNoticeKind
{
	Started,

	/// <summary>A save was accepted and every area controller rebuilt on it.</summary>
	SettingsSaved
}

/// <summary>One house-wide thing the engine did.</summary>
/// <remarks><c>At</c> is wall-clock, not scheduler time: nothing schedules a rebuild.</remarks>
public sealed record EngineNotice(EngineNoticeKind Kind, DateTimeOffset At);
