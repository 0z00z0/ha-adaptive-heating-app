using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Web.Presentation;

/// <summary>Where one value on a levels row came from.</summary>
public enum LevelSource
{
	/// <summary>The house's schedule decides it, and a later edit to the schedule reaches this room.</summary>
	Schedule,

	/// <summary>This room states it for itself, and the schedule no longer reaches it.</summary>
	Room
}

/// <summary>One period as the room will run it: the level in force, and whether the room or the schedule decided it.</summary>
public sealed record RoomLevelRow(
	string PeriodId,
	string Name,
	double? Temperature,
	LevelSource Source)
{
	/// <summary>Whether this room states anything at all for this period, which is what draws the row's mark.</summary>
	public bool IsOwn => Source == LevelSource.Room;
}

/// <summary>A row this room states for a period the schedule no longer has.</summary>
public sealed record RoomLevelOrphan(string PeriodId, double? Temperature)
{
	// A row with values but no period id survives normalisation, so the blank case reaches the screen.
	public string Name => PeriodId is { Length: > 0 } named ? named : "(a row with no period)";

	/// <summary>What the orphan holds, written out, so the row says what removing it would throw away.</summary>
	public string Says => Temperature is { } celsius ? $"{celsius.ToString("0.#", CultureInfo.CurrentCulture)} °C" : "nothing";
}

/// <summary>What a room runs instead of the schedule, projected for the surface that shows and edits it.</summary>
/// <remarks>
///     The writing half is the only place <see cref="AreaConfig.PeriodTemperatures"/> is mutated, so the rule that an empty
///     row is dropped, never stored, lives in one place.
/// </remarks>
public static class RoomLevels
{
	/// <summary>One row per period in the schedule, in the schedule's own order, showing what this room will run.</summary>
	public static IReadOnlyList<RoomLevelRow> Rows(IReadOnlyList<TimePeriodConfig> periods, AreaConfig? room)
	{
		ArgumentNullException.ThrowIfNull(periods);

		List<RoomLevelRow> rows = [];

		foreach (TimePeriodConfig period in periods)
		{
			RoomPeriodTemperature? own = Stated(room, period.Key);

			rows.Add(new RoomLevelRow(
				period.Key,
				period.Name,
				own?.Temperature ?? period.Temperature,
				own?.Temperature is not null ? LevelSource.Room : LevelSource.Schedule));
		}

		return rows;
	}

	/// <summary>The rows this room states for periods the schedule no longer has. Empty rows are not reported.</summary>
	public static IReadOnlyList<RoomLevelOrphan> Orphans(IReadOnlyList<TimePeriodConfig> periods, AreaConfig? room)
	{
		ArgumentNullException.ThrowIfNull(periods);

		if (room is null)
			return [];

		return
		[
			.. room.PeriodTemperatures
				.Where(level => !level.IsEmpty)
				.Where(level => periods.ByKey(level.PeriodId) is null)
				.Select(level => new RoomLevelOrphan(level.PeriodId ?? "", level.Temperature))
		];
	}

	/// <summary>How many of the periods on screen this room states for itself.</summary>
	/// <remarks>Counted off <see cref="Rows"/>, so it cannot disagree with the marks in the table.</remarks>
	public static int OwnCount(IReadOnlyList<TimePeriodConfig> periods, AreaConfig? room) =>
		Rows(periods, room).Count(row => row.IsOwn);

	/// <summary>Sets this room's temperature for a period, or sends it back to the schedule with <c>null</c>.</summary>
	public static void SetTemperature(AreaConfig room, string periodId, double? celsius)
	{
		ArgumentNullException.ThrowIfNull(room);

		Edit(room, periodId, level => level.Temperature = celsius);
	}

	/// <summary>Drops everything this room says about a period, which is the road back from an orphan.</summary>
	/// <returns>Whether anything was removed.</returns>
	public static bool Remove(AreaConfig room, string periodId)
	{
		ArgumentNullException.ThrowIfNull(room);

		return room.PeriodTemperatures.RemoveAll(level => level.PeriodId.SameName(periodId)) > 0;
	}

	/// <summary>The schedule's periods this room states nothing for, which is where an orphan row may be sent.</summary>
	public static IReadOnlyList<TimePeriodConfig> FreePeriods(IReadOnlyList<TimePeriodConfig> periods, AreaConfig? room)
	{
		ArgumentNullException.ThrowIfNull(periods);

		return [.. periods.Where(period => Stated(room, period.Key) is null)];
	}

	/// <summary>Points an orphaned row at a real period, carrying its temperature across.</summary>
	/// <remarks>
	///     Refuses a target the room already states, so a move cannot silently overwrite a level that is on screen.
	///     <see cref="FreePeriods"/> is what the picker offers, and the check is repeated here because the schedule
	///     can change under an open page.
	/// </remarks>
	public static bool Repoint(AreaConfig room, string fromPeriodId, string toPeriodId)
	{
		ArgumentNullException.ThrowIfNull(room);

		if (string.IsNullOrWhiteSpace(toPeriodId) || fromPeriodId.SameName(toPeriodId))
			return false;

		RoomPeriodTemperature? orphan = Find(room, fromPeriodId);
		if (orphan is null || orphan.IsEmpty || Stated(room, toPeriodId) is not null)
			return false;

		orphan.PeriodId = toPeriodId;
		Prune(room);
		return true;
	}

	// Read path. Skips empty rows: a cleared row above a real one would otherwise show a level the room does not
	// run. SameName throughout, matching the engine, which trims before matching; an untrimmed comparison misses a
	// row and the edit writes a second one for the same period.
	private static RoomPeriodTemperature? Stated(AreaConfig? room, string periodId) =>
		room?.PeriodTemperatures.FirstOrDefault(level => !level.IsEmpty && level.PeriodId.SameName(periodId));

	// Write path. Any row, empty included, so an edit reuses a cleared row instead of adding a second one beside it.
	private static RoomPeriodTemperature? Find(AreaConfig? room, string periodId) =>
		room?.PeriodTemperatures.FirstOrDefault(level => level.PeriodId.SameName(periodId));

	/// <summary>Applies one change to the room's row for a period, creating it and dropping it as needed.</summary>
	private static void Edit(AreaConfig room, string periodId, Action<RoomPeriodTemperature> change)
	{
		// Stated first, so the edit lands on the row the page is showing and never on a cleared row above it.
		RoomPeriodTemperature? level = Stated(room, periodId) ?? Find(room, periodId);

		if (level is null)
		{
			level = new RoomPeriodTemperature { PeriodId = periodId };
			room.PeriodTemperatures.Add(level);
		}

		change(level);
		Prune(room);
	}

	// After each edit, never at save time: a row left behind by a clear counts as an override to everything that
	// reads AreaConfig.PeriodTemperatures.
	private static void Prune(AreaConfig room) => room.PeriodTemperatures.RemoveAll(level => level.IsEmpty);
}
