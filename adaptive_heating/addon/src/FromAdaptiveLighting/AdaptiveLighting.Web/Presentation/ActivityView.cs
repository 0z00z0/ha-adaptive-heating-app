using AdaptiveHeating.Page.Settings;

namespace AdaptiveLighting.Web.Presentation;

/// <param name="What">What the add-on did, or could not do. Never empty.</param>
/// <param name="Why">The reading or condition behind it, or <c>null</c> when the event speaks for itself.</param>
/// <param name="More">An explanation too long for the row, drawn behind an (i).</param>
public sealed record ActivityLine(string What, string? Why, string? More = null);

/// <summary>One rendered line of the record: the words, when they were said, and which room they are about.</summary>
public sealed record ActivityRow(RecordedReport Entry, ActivityLine Line, bool IsAboutTheHouse)
{
	public DateTimeOffset At => Entry.At;

	/// <summary>The row's key. Dense and never reused, so it survives eviction from the record.</summary>
	public long Sequence => Entry.Sequence;

	public string? Room => IsAboutTheHouse ? null : Entry.Room;

	public string? RoomId => Entry.RoomId;
}

/// <summary>What a row is about, as the activity page's filter chips divide it.</summary>
/// <remarks>A report is in one category and no other, so switching a chip off removes the reports it counts.</remarks>
[Flags]
public enum ActivityCategory
{
	None = 0,

	/// <summary>A room was told the temperature to hold, began a warm-up, or was switched in a mode's place.</summary>
	TemperatureChange = 2,

	/// <summary>Somebody set a thermostat themselves, or a manual change ran its course.</summary>
	ManualChange = 8,

	/// <summary>The add-on could not do something, and the row says why.</summary>
	Declined = 16,

	/// <summary>Housekeeping: start-up, a save, learnt numbers and the record of warm-ups.</summary>
	Background = 64,

	/// <summary>The house moved to a different mode.</summary>
	Mode = 128
}

/// <param name="Count">How many of the reports the room filter left, not of the whole record.</param>
public sealed record ActivityFilterChip(
	ActivityCategory Category,
	string Label,
	string Title,
	int Count,
	bool IsOn);

/// <param name="Heading">What that date is called: <c>Today</c>, <c>Yesterday</c>, or the date.</param>
public sealed record ActivityDay(DateOnly Day, string Heading, IReadOnlyList<ActivityRow> Rows);

/// <summary>A room in the activity page's filter: the key it is chosen by, and the name it is shown under.</summary>
public sealed record ActivityRoomOption(string Key, string Name);

/// <summary>The activity page's decisions, in one testable place: which room a report belongs to, which category,
/// and which day it falls under.</summary>
// The words come from the add-on's own describer, which the logbook and the room log read too.
public static class ActivityView
{
	public const string AllRooms = "";

	/// <summary>Every category: what "show everything" puts the chips back to.</summary>
	public const ActivityCategory AllCategories =
		ActivityCategory.TemperatureChange
		| ActivityCategory.ManualChange
		| ActivityCategory.Declined
		| ActivityCategory.Mode
		| ActivityCategory.Background;

	/// <summary>What the page opens with. Background is the only category that starts off.</summary>
	public const ActivityCategory DefaultCategories = AllCategories & ~ActivityCategory.Background;

	/// <summary>What the dashboard's summary carries: what the board's lanes cannot draw.</summary>
	public const ActivityCategory SummaryCategories = ActivityCategory.ManualChange | ActivityCategory.Declined | ActivityCategory.Mode;

	private sealed record CategoryName(ActivityCategory Category, string Label, string Title);

	private static readonly CategoryName[] Catalogue =
	[
		new(ActivityCategory.TemperatureChange, "Temperature change",
			"A room was told the temperature to hold, began warming up, or was switched on or off in a mode's place."),
		new(ActivityCategory.ManualChange, "Manual changes",
			"Somebody or another automation set a thermostat, and what happened when that ran out."),
		new(ActivityCategory.Declined, "Could not",
			"Something the add-on could not do — with the reason."),
		new(ActivityCategory.Mode, "Mode changes", "The house moved to a different mode."),
		new(ActivityCategory.Background, "Background tasks",
			"Start-up, saves, learnt numbers and finished warm-ups. Starts hidden — the highest volume, the lowest signal.")
	];

	/// <summary>The reports filed under the room <paramref name="key"/> stands for.</summary>
	public static IReadOnlyList<RecordedReport> InRoomByKey(IEnumerable<RecordedReport> entries, string? key)
	{
		ArgumentNullException.ThrowIfNull(entries);

		if (string.IsNullOrWhiteSpace(key))
			return [.. entries];

		return [.. entries.Where(entry => string.Equals(entry.RoomId, key, StringComparison.OrdinalIgnoreCase))];
	}

	/// <summary>The rooms that have reported, each under the name it reported most recently, sorted by name.</summary>
	public static IReadOnlyList<ActivityRoomOption> RoomOptions(IEnumerable<RecordedReport> entries)
	{
		ArgumentNullException.ThrowIfNull(entries);

		Dictionary<string, ActivityRoomOption> byKey = new(StringComparer.OrdinalIgnoreCase);

		// A house-wide report names no room, and an option with no room is one the filter cannot mean.
		foreach (RecordedReport entry in entries.OrderBy(entry => entry.Sequence))
			if (entry.RoomId is { Length: > 0 } key)
				byKey[key] = new ActivityRoomOption(key, entry.Room is { Length: > 0 } name ? name : key);

		return [.. byKey.Values.OrderBy(option => option.Name, StringComparer.CurrentCulture)];
	}

	/// <summary>Only the reports in <paramref name="categories"/>.</summary>
	public static IReadOnlyList<RecordedReport> InCategories(IEnumerable<RecordedReport> entries, ActivityCategory categories)
	{
		ArgumentNullException.ThrowIfNull(entries);

		if (categories == AllCategories)
			return [.. entries];

		return [.. entries.Where(entry => (entry.Category & categories) != ActivityCategory.None)];
	}

	/// <summary>The chips the page draws. Every category is offered, including the ones holding nothing.</summary>
	/// <param name="entries">The reports the room filter left, so the counts describe the room.</param>
	/// <param name="chosen">The categories switched on.</param>
	public static IReadOnlyList<ActivityFilterChip> Chips(IEnumerable<RecordedReport> entries, ActivityCategory chosen)
	{
		ArgumentNullException.ThrowIfNull(entries);

		int[] counts = new int[Catalogue.Length];

		foreach (RecordedReport entry in entries)
		{
			for (int index = 0; index < Catalogue.Length; index++)
			{
				if ((entry.Category & Catalogue[index].Category) != ActivityCategory.None)
					counts[index]++;
			}
		}

		return
		[
			.. Catalogue.Select((name, index) => new ActivityFilterChip(
				name.Category,
				name.Label,
				name.Title,
				counts[index],
				(chosen & name.Category) != ActivityCategory.None))
		];
	}

	/// <summary>What the filters are keeping off the page, or <c>null</c> when they are keeping nothing off it.</summary>
	public static string? HiddenNote(int held, int shown)
	{
		int hidden = held - shown;

		return hidden <= 0 ? null : $"{hidden} hidden";
	}

	/// <summary>Why <see cref="HiddenNote"/>'s reports are off the page, for the (i) beside it.</summary>
	public static string HiddenWhy(string? room, ActivityCategory categories)
	{
		bool byRoom = !string.IsNullOrWhiteSpace(room);
		bool byCategory = categories != AllCategories;

		return (byRoom, byCategory) switch
		{
			(true, true) => "They came from other rooms, or fall into categories that are switched off.",
			(true, false) => "They came from other rooms.",
			_ => "They fall into categories that are switched off."
		};
	}

	/// <summary>The rows the timeline draws, newest first, at most <paramref name="limit"/> of them.</summary>
	public static IReadOnlyList<ActivityRow> Rows(IEnumerable<RecordedReport> entries, int? limit = null)
	{
		ArgumentNullException.ThrowIfNull(entries);

		IEnumerable<ActivityRow> rows = entries
			.OrderByDescending(entry => entry.Sequence)
			.Select(entry => new ActivityRow(entry, new ActivityLine(entry.What, entry.Why), entry.RoomId is not { Length: > 0 }));

		return limit is { } most ? [.. rows.Take(most)] : [.. rows];
	}

	/// <summary>Cuts the timeline into days, keeping the order it was given.</summary>
	/// <remarks>Local dates, never UTC, grouped consecutively so the caller's order survives.</remarks>
	public static IReadOnlyList<ActivityDay> GroupByDay(IEnumerable<ActivityRow> rows, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(rows);

		DateOnly today = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
		List<ActivityDay> days = [];
		List<ActivityRow> current = [];
		DateOnly day = default;

		foreach (ActivityRow row in rows)
		{
			DateOnly rowDay = DateOnly.FromDateTime(row.At.ToLocalTime().DateTime);

			if (current.Count > 0 && rowDay != day)
			{
				days.Add(new ActivityDay(day, DayHeading(day, today), current));
				current = [];
			}

			day = rowDay;
			current.Add(row);
		}

		if (current.Count > 0)
			days.Add(new ActivityDay(day, DayHeading(day, today), current));

		return days;
	}

	/// <summary>What a day is called on the page. Only today and yesterday get a name.</summary>
	public static string DayHeading(DateOnly day, DateOnly today)
	{
		int back = today.DayNumber - day.DayNumber;

		return back switch
		{
			0 => "Today",
			1 => "Yesterday",
			_ => day.ToString("dddd d MMMM", CultureInfo.CurrentCulture)
		};
	}
}
