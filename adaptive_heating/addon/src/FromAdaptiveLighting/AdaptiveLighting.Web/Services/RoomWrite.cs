namespace Adaptive.Settings;

/// <summary>The slot of the document the room page owns, and that slot as it stood on disk when the page read it.</summary>
public sealed record RoomWriteToken(string? AreaId, string Stamp);

/// <summary>What one scoped write did, and the token the page carries on with, unchanged unless the write reached the disk.</summary>
public sealed record RoomWriteResult(SaveResult Result, RoomWriteToken Token);

/// <summary>The room page's write: one area slot, applied to the document as it is on disk at the moment of writing.</summary>
/// <remarks>
///     The room page reads the whole document once, when it opens, and stays open, so writing that copy back would
///     revert anything written in between, the engine's own writes included. This reads the file again and applies
///     the page's room onto the fresh document. The save is still the host's, so the stamp check, normalisation and
///     validation are unchanged.
/// </remarks>
public static class RoomWrite
{
	public static RoomWriteToken Open(HeatingConfig document, string? areaId) =>
		new(areaId, ConfigStamp.OfArea(document, areaId));

	/// <summary>
	///     Writes <paramref name="room"/> into its slot of the current document, or removes the slot when
	///     <paramref name="room"/> is <c>null</c>.
	/// </summary>
	/// <exception cref="HeatingConfigException">The document could not be read.</exception>
	public static RoomWriteResult Save(HeatingDocumentHost host, RoomWriteToken token, AreaConfig? room)
	{
		ArgumentNullException.ThrowIfNull(host);
		ArgumentNullException.ThrowIfNull(token);

		HeatingConfig working = host.Store.Load();

		ApplyArea(working, token.AreaId, room);

		string? areaId = token.AreaId;

		SaveResult saved = host.Save(working, new StampCheck(token.Stamp, onDisk => ConfigStamp.OfArea(onDisk, areaId)));

		if (!saved.Written)
			return new RoomWriteResult(saved, token);

		// Re-read, never stamped off the object just sent: the store normalises on the way out, so the next save
		// has to match the bytes on disk.
		try
		{
			return new RoomWriteResult(saved, Open(host.Store.Load(), room?.AreaId ?? token.AreaId));
		}
		catch (HeatingConfigException)
		{
			// The write is already on disk. Failing here would report a save that happened as one that did not;
			// the caller keeps the old token and the next write against it reports a conflict a reload clears.
			return new RoomWriteResult(saved, token);
		}
	}

	/// <summary>Puts <paramref name="area"/> in the slot <paramref name="areaId"/> names, leaving every other area as it was read.</summary>
	private static void ApplyArea(HeatingConfig document, string? areaId, AreaConfig? area)
	{
		int index = areaId is { Length: > 0 }
			? document.Areas.FindIndex(
				candidate => string.Equals(candidate.AreaId, areaId, StringComparison.Ordinal))
			: -1;

		if (index < 0)
		{
			// Only reachable on a slot that was already absent when the page read it. Appending keeps this total
			// instead of silently dropping a write.
			if (area is not null)
				document.Areas.Add(area);

			return;
		}

		if (area is null)
			document.Areas.RemoveAt(index);
		else
			document.Areas[index] = area;
	}
}
