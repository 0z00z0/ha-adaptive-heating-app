namespace AdaptiveHeating.Page.Settings;

/// <summary>One line of a room's log, already in words: when, what, and the condition behind it.</summary>
public sealed record RoomLogRow(long Sequence, DateTimeOffset At, string What, string Why);

/// <summary>What the add-on has recently done in one room, newest first, which the room page's log fold reads.</summary>
/// <remarks>The same record the Home Assistant logbook is written from, so the two never disagree.</remarks>
public interface IRoomActivity
{
	/// <summary>Every report the record holds about <paramref name="roomId"/>, newest first.</summary>
	IReadOnlyList<RoomLogRow> For(string roomId);
}

/// <summary>No record at all, for a host that keeps none.</summary>
public sealed class NoRoomActivity : IRoomActivity
{
	public IReadOnlyList<RoomLogRow> For(string roomId) => [];
}
