using Adaptive.Settings;

using AdaptiveHeating.AddOn.Reporting;
using AdaptiveHeating.Page.Settings;

namespace AdaptiveHeating.AddOn.Integration;

/// <summary>A room whose card names another area moves its settings entry there, as the area picker does, with no call.</summary>
// Only the entry's area changes. Everything kept per room is keyed by its room id, which the move leaves alone.
internal sealed class RoomsMovedInHomeAssistant
{
	private readonly TellsTheThermostats _tells;
	private readonly HeatingDocumentHost _document;
	private readonly ReportsWhatHappened _reports;
	private readonly ILogger<RoomsMovedInHomeAssistant> _logger;

	private readonly HashSet<(string, string)> _refusedSaid = [];

	public RoomsMovedInHomeAssistant(
		TellsTheThermostats tells,
		HeatingDocumentHost document,
		ReportsWhatHappened reports,
		ILogger<RoomsMovedInHomeAssistant> logger)
	{
		_tells = tells ?? throw new ArgumentNullException(nameof(tells));
		_document = document ?? throw new ArgumentNullException(nameof(document));
		_reports = reports ?? throw new ArgumentNullException(nameof(reports));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <summary>Moves every room whose card names another area than its entry. Runs before the rooms are merged.</summary>
	/// <returns>How many rooms moved.</returns>
	public async Task<int> FollowAsync(IReadOnlyList<EntityState> states, DateTimeOffset now, CancellationToken token)
	{
		ArgumentNullException.ThrowIfNull(states);

		HeatingDocument held = _tells.Read();
		int moved = 0;

		foreach ((string area, string roomId) in RoomsFromTheAreas.RoomInEachArea(states))
		{
			if (held.Rooms.FirstOrDefault(room => string.Equals(room.Id, roomId, StringComparison.Ordinal)) is not { AreaId: { Length: > 0 } from }
				|| string.Equals(from, area, StringComparison.Ordinal))
				continue;

			if (await MoveAsync(roomId, from, area, now, token).ConfigureAwait(false))
				moved++;
		}

		return moved;
	}

	private async Task<bool> MoveAsync(string roomId, string from, string to, DateTimeOffset now, CancellationToken token)
	{
		HeatingConfig document = _document.Store.Load();

		if (document.Areas.FirstOrDefault(area => string.Equals(area.AreaId, from, StringComparison.Ordinal)) is not { } room)
			return false;

		// The integration refuses an area another of its rooms is in; an entry here with no room behind it is kept.
		if (document.Areas.Any(area => string.Equals(area.AreaId, to, StringComparison.Ordinal)))
		{
			if (_refusedSaid.Add((roomId, to)))
				_logger.LogWarning("Room {Room} stays in area {From}: the settings already hold area {To}.", roomId, from, to);

			return false;
		}

		RoomWriteToken slot = RoomWrite.Open(document, from);
		room.AreaId = to;
		RoomWriteResult written = RoomWrite.Save(_document, slot, room);

		if (!written.Result.Written)
		{
			_logger.LogWarning("Room {Room} did not follow its thermostat to area {To}: {Why}", roomId, to, written.Result.Message);

			return false;
		}

		await _reports
			.ReportAsync(HeatingReport.About(WhatHappened.RoomMoved, now, roomId), token)
			.ConfigureAwait(false);

		return true;
	}
}
