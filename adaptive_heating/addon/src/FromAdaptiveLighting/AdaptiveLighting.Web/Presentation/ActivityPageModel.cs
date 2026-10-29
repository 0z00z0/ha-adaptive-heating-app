using AdaptiveHeating.Page.Settings;

namespace AdaptiveLighting.Web.Presentation;

/// <summary>Everything the activity page holds and decides; it never writes the document.</summary>
public sealed class ActivityPageModel : IDisposable
{
	private readonly IActivityRecord _record;
	private readonly ILogger _logger;
	private readonly Func<Action, Task> _dispatch;

	private IReadOnlyList<RecordedReport> _entries = [];
	private IReadOnlyList<RecordedReport> _inRoom = [];
	private IReadOnlyList<RecordedReport> _visible = [];
	private IReadOnlyList<ActivityRow> _rows = [];
	private IReadOnlyList<ActivityFilterChip> _chips = [];

	/// <summary>The chosen room, held as its id so a rename in Home Assistant does not clear the filter.</summary>
	private string _room = ActivityView.AllRooms;

	private ActivityCategory _categories = ActivityView.DefaultCategories;

	private long _shownThrough;
	private long _newest;
	private IDisposable? _subscription;

	// dispatch runs work on the thread the interface renders on; a test runs it inline.
	public ActivityPageModel(IActivityRecord record, ILogger logger, Func<Action, Task> dispatch)
	{
		_record = record ?? throw new ArgumentNullException(nameof(record));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		_dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
	}

	/// <summary>Raised when the record has moved and the page should redraw.</summary>
	/// <remarks>Only moves the "new reports" count; the render it causes must not re-read the record itself.</remarks>
	public event Action? Changed;

	/// <summary>Reads what the record is holding and starts watching it for news.</summary>
	public void Start()
	{
		ShowNew();

		// The record raises nothing, so it is asked once a second, as lighting samples its own news.
		_subscription = Observable.Interval(TimeSpan.FromSeconds(1))
			.SubscribeSafe(_ => OnBeat(), _logger);
	}

	private void OnBeat()
	{
		long newest = _record.Read().Newest;

		if (newest == _newest)
			return;

		_ = _dispatch(() =>
		{
			_newest = newest;
			Changed?.Invoke();
		});
	}

	/// <summary>What the timeline draws: the rows left once the room and category filters are both applied.</summary>
	public IReadOnlyList<ActivityRow> Rows => _rows;

	/// <summary>The category chips. Each chip's count is within the chosen room, not the house.</summary>
	public IReadOnlyList<ActivityFilterChip> Chips => _chips;

	public IReadOnlyList<ActivityRoomOption> RoomOptions => ActivityView.RoomOptions(_entries);

	public string Room => _room;

	/// <summary>How many reports have arrived since the timeline was last drawn.</summary>
	/// <remarks>Sequence numbers, not a count of entries, so eviction from the record cannot skew it.</remarks>
	public long Pending => _newest - _shownThrough;

	public string PendingWord => Pending == 1 ? "1 new report — show it" : $"{Pending} new reports — show them";

	/// <summary>What the filters are keeping off the page, or <c>null</c> when they keep nothing off it.</summary>
	public string? HiddenNote => ActivityView.HiddenNote(_entries.Count, _visible.Count);

	/// <summary>Why <see cref="HiddenNote"/>'s reports are off the page.</summary>
	public string HiddenWhy => ActivityView.HiddenWhy(_room, _categories);

	/// <summary>What the page is holding, and the cap it is held to.</summary>
	public string FootLine
	{
		get
		{
			string held = _entries.Count == 1 ? "1 report" : $"{_entries.Count} reports";
			string shown = _visible.Count == _entries.Count ? held : $"{_visible.Count} of {held}";

			return _entries.Count < _record.Capacity
				? $"{shown} kept"
				: $"{shown} · last {_record.Capacity} kept";
		}
	}

	/// <summary>What the (i) beside <see cref="FootLine"/> explains.</summary>
	public string FootWhy => _entries.Count < _record.Capacity
		? "The record survives a restart. Times come from the machine running adaptive heating, which is not always the one this page is read on."
		: $"Only the most recent {_record.Capacity} reports are kept; older ones have been dropped.";

	/// <summary>Draws everything the record is holding and moves the read mark to the newest report.</summary>
	/// <remarks>One read, never two: a report landing between two reads would be marked shown while absent.</remarks>
	public void ShowNew()
	{
		RecordRead read = _record.Read();

		_entries = read.Reports;
		_shownThrough = read.Newest;
		_newest = read.Newest;

		ApplyFilter();
	}

	/// <remarks><c>null</c> is what the bound select reports for its own "All rooms" option's round trip.</remarks>
	public void SelectRoom(string? room)
	{
		_room = room ?? ActivityView.AllRooms;
		ApplyFilter();
	}

	public void Toggle(ActivityCategory category)
	{
		_categories ^= category;
		ApplyFilter();
	}

	/// <summary>Every category on and every room shown, without re-reading the record.</summary>
	public void ShowEverything()
	{
		_room = ActivityView.AllRooms;
		_categories = ActivityView.AllCategories;
		ApplyFilter();
	}

	// Room narrows first; the chips then count within that room.
	private void ApplyFilter()
	{
		_inRoom = ActivityView.InRoomByKey(_entries, _room);
		_visible = ActivityView.InCategories(_inRoom, _categories);
		_rows = ActivityView.Rows(_visible);
		_chips = ActivityView.Chips(_inRoom, _categories);
	}

	/// <inheritdoc/>
	public void Dispose() => _subscription?.Dispose();
}
