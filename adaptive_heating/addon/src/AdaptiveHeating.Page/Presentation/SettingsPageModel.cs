using AdaptiveHeating.Page.Settings;
using AdaptiveHeating.Planner;

namespace AdaptiveHeating.Page.Presentation;

/// <summary>Which part of the settings the page is showing.</summary>
public enum SettingsSection
{
	House,
	Schedule,
	Calendars,
	HouseModes
}

/// <summary>
/// What the settings page reads and what a click on it does. The page holds no rule of its own: every change
/// goes to the store, and what comes back is what the table draws.
/// </summary>
public sealed class SettingsPageModel : IDisposable
{
	private readonly ISettingsStore _store;

	private HeatingDocument _document;

	public SettingsPageModel(ISettingsStore store)
	{
		ArgumentNullException.ThrowIfNull(store);

		_store = store;
		_document = store.Read();
		_store.Changed += OnStoreChanged;
	}

	/// <summary>Raised when the table on screen no longer states what is held.</summary>
	public event Action? Changed;

	public HeatingDocument Document => _document;

	public SettingsSection Section { get; private set; } = SettingsSection.House;

	/// <summary>What the last save did. Empty until something has been saved.</summary>
	public SaveOutcome LastOutcome { get; private set; } = SaveOutcome.Ok;

	/// <summary>The room the last save was about, so a refusal is shown beside the room it refused.</summary>
	public string? LastSavedRoomId { get; private set; }

	/// <summary>True where the last save was refused, which is the only state that needs a red mark.</summary>
	public bool LastSaveWasRefused => !LastOutcome.Written;

	public bool IsActive(SettingsSection section) => Section == section;

	/// <summary>Opens a section, from the query on load or from the sub-navigation afterwards.</summary>
	/// <remarks>The last outcome leaves with the tab: a refusal still on screen under another tab reads as a
	/// refusal of what is there now, which is a save nobody made.</remarks>
	public void Show(SettingsSection section)
	{
		if (Section == section)
			return;

		Section = section;
		LastOutcome = SaveOutcome.Ok;
		LastSavedRoomId = null;
		Raise();
	}

	/// <summary>Reads the section a link asked for. An unknown value opens the House tab.</summary>
	public void Start(string? sectionQuery)
	{
		Section = sectionQuery?.ToLowerInvariant() switch
		{
			// The room list is on the House tab, so lighting's links asking for the rooms land there.
			"house" or "areas" or "rooms" => SettingsSection.House,
			"schedule" => SettingsSection.Schedule,
			"calendars" => SettingsSection.Calendars,
			"housemodes" => SettingsSection.HouseModes,
			_ => SettingsSection.House
		};
	}

	/// <summary>Writes which calendar the arrivals are read from and which one the record is written to.</summary>
	public void SaveCalendars(string? arrivals, string? record) =>
		Record(null, _store.SaveCalendars(arrivals, record));

	/// <inheritdoc/>
	public void Dispose() => _store.Changed -= OnStoreChanged;

	private void Record(string? roomId, SaveOutcome outcome)
	{
		LastOutcome = outcome;
		LastSavedRoomId = roomId;
		_document = _store.Read();
		Raise();
	}

	private void OnStoreChanged()
	{
		_document = _store.Read();
		Raise();
	}

	private void Raise() => Changed?.Invoke();
}
