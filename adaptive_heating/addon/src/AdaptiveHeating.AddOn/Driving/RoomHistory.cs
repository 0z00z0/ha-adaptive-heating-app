using System.Collections.Concurrent;

namespace AdaptiveHeating.AddOn.Driving;

/// <summary>Each room's last change at its thermostat and who made it, kept across a restart in lighting's room-history note.</summary>
// Lighting's host keeps the same live dictionary, written coalesced through the note; keyed by room id.
internal sealed class RoomHistory
{
	private readonly IRoomHistoryStore? _store;
	private readonly ConcurrentDictionary<string, AreaHistory> _live;

	public RoomHistory(IRoomHistoryStore? store)
	{
		_store = store;
		_live = new ConcurrentDictionary<string, AreaHistory>(
			store?.Load() ?? new Dictionary<string, AreaHistory>(),
			StringComparer.OrdinalIgnoreCase);
	}

	public AreaHistory? For(string roomId) => _live.TryGetValue(roomId, out AreaHistory? history) ? history : null;

	/// <summary>A change at the room's thermostat: when, then the words for another origin, or a person's user id, or an automation's entity id.</summary>
	public void Changed(string roomId, DateTimeOffset at, string? by, string? byUserId, string? byAutomationId)
	{
		_live[roomId] = new AreaHistory(LastMotionAt: null, at, by, byUserId, byAutomationId);
		_store?.TrySave(_live);
	}

	/// <summary>Keeps only the rooms the document holds.</summary>
	public void KeepOnly(IReadOnlyCollection<string> roomIds)
	{
		bool dropped = false;

		foreach (string key in _live.Keys.Where(key => !roomIds.Contains(key, StringComparer.OrdinalIgnoreCase)).ToList())
			dropped |= _live.TryRemove(key, out _);

		if (dropped)
			_store?.TrySave(_live);
	}

	/// <summary>Writes whatever is waiting, on the way out.</summary>
	public void Flush() => _store?.Flush();
}
