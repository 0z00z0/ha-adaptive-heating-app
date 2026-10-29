using AdaptiveLighting.Web.Presentation;

namespace AdaptiveHeating.Page.Settings;

/// <summary>One report of the add-on's record, already in words. Room and RoomId are null for a house-wide one.</summary>
/// <remarks>RoomId is the area the room is in now, which the copied pages link to and filter by.</remarks>
public sealed record RecordedReport(
	long Sequence,
	DateTimeOffset At,
	string? RoomId,
	string? Room,
	string What,
	string? Why,
	ActivityCategory Category);

/// <summary>The reports, newest first, and the sequence reached at that same instant.</summary>
public sealed record RecordRead(IReadOnlyList<RecordedReport> Reports, long Newest);

/// <summary>What the add-on has recently done: the record the room log and the Home Assistant logbook come from.</summary>
public interface IActivityRecord
{
	int Capacity { get; }

	RecordRead Read();
}

public sealed class NoActivityRecord : IActivityRecord
{
	public int Capacity => 500;

	public RecordRead Read() => new([], 0);
}
