using AdaptiveHeating.Page.Presentation;
using AdaptiveHeating.Page.Settings;
using AdaptiveHeating.Planner;

namespace AdaptiveHeating.AddOn.Settings;

/// <summary>The settings page's store with a file under it, so what a person typed is still there after a restart.</summary>
internal sealed class DurableSettingsStore : ISettingsStore
{
	// Named after the configuration document stem, so two hosts sharing a directory cannot collide.
	private const string NameSuffix = "-settings.json";

	private const string NotReadBack = "Saved, and the file could not be read back.";

	private readonly InMemorySettingsStore _held;
	private readonly JsonNoteFile _file;
	private readonly TimeProvider _clock;
	private readonly ILogger _logger;

	// The note file is not thread-safe, and a read-back outside the lock would answer another writer's file.
	private readonly Lock _writeGate = new();

	private DurableSettingsStore(InMemorySettingsStore held, JsonNoteFile file, TimeProvider clock, ILogger logger)
	{
		_held = held;
		_file = file;
		_clock = clock;
		_logger = logger;

		_held.Changed += () => Changed?.Invoke();
	}

	/// <inheritdoc/>
	public event Action? Changed;

	/// <summary>Where the file sits, which is what the start-up report names.</summary>
	public string FilePath => _file.FilePath;

	/// <summary>Opens the file and answers a store holding whatever it restored, or <paramref name="whenThereIsNoFile"/>.</summary>
	/// <remarks>Its own file, outside the state-note registry, so no clock comparison can discard the settings a
	/// cabin back from a power cut reads as saved in the future.</remarks>
	public static DurableSettingsStore Open(
		StateStoreRegistry registry,
		TimeProvider clock,
		HeatingDocument whenThereIsNoFile,
		ILogger logger,
		Func<HeatingDocument, HeatingDocument>? withTheValues = null)
	{
		ArgumentNullException.ThrowIfNull(registry);
		ArgumentNullException.ThrowIfNull(clock);
		ArgumentNullException.ThrowIfNull(whenThereIsNoFile);
		ArgumentNullException.ThrowIfNull(logger);

		JsonNoteFile file = new(registry.DocumentPath, NameSuffix, logger, inStateFolder: true);

		HeatingDocument document = Restore(file, logger) is { } settings
			// The integration has said nothing yet whatever the file held, and the add-on's own version is this build's.
			? settings with { Versions = new HalfVersions(HalfVersions.AddOnVersion, null, Vocabulary.Version) }
			: whenThereIsNoFile;

		return new DurableSettingsStore(new InMemorySettingsStore(document, withTheValues), file, clock, logger);
	}

	/// <inheritdoc/>
	public HeatingDocument Read() => _held.Read();

	/// <inheritdoc/>
	public void Receive(HeatingDocument document)
	{
		_held.Receive(document);
		Save();
	}

	/// <inheritdoc/>
	// Not written: a reading is read again within a minute of a restart, so writing one would cost a file write on
	// every pass over the rooms and buy nothing.
	public void ReceiveWhatTheRoomsAreDoing(
		InForceRecord inForce,
		IReadOnlyDictionary<string, RoomNow> now,
		IReadOnlyDictionary<string, RoomVerdict>? verdicts = null) =>
		_held.ReceiveWhatTheRoomsAreDoing(inForce, now, verdicts);

	/// <inheritdoc/>
	// Not written either: the house is read again within a minute of a restart, and a stored list of calendars
	// would go stale against Home Assistant.
	public void ReceiveTheCalendarsOffered(IReadOnlyList<CalendarOnOffer> calendars) =>
		_held.ReceiveTheCalendarsOffered(calendars);

	/// <inheritdoc/>
	public SaveOutcome SaveCalendars(string? arrivals, string? record) =>
		Written(_held.SaveCalendars(arrivals, record));

	/// <summary>Rewrites every stamp made against an unset clock to the moment the clock was set.</summary>
	public void WhenTheClockIsSet(DateTimeOffset networkTime) =>
		Receive(_held.Read().WhenTheClockIsSet(networkTime));

	/// <inheritdoc/>
	public SaveOutcome SaveModeStamps(string roomId, IReadOnlyCollection<HeatingMode> modes, Stamp stamp) =>
		Written(_held.SaveModeStamps(roomId, modes, stamp));

	/// <inheritdoc/>
	public SaveOutcome SaveSensorSettings(string roomId, RoomSensorSettings settings) =>
		Written(_held.SaveSensorSettings(roomId, settings));

	/// <inheritdoc/>
	public SaveOutcome SaveModeSwitch(string roomId, HeatingMode mode, bool on, Stamp stamp) =>
		Written(_held.SaveModeSwitch(roomId, mode, on, stamp));

	/// <inheritdoc/>
	public SaveOutcome ResetLearnt(string roomId, LearntKind kind) =>
		Written(_held.ResetLearnt(roomId, kind));

	/// <inheritdoc/>
	public SaveOutcome SetLearntByHand(string roomId, LearntKind kind, double? value) =>
		Written(_held.SetLearntByHand(roomId, kind, value));

	/// <inheritdoc/>
	public Task<SaveOutcome> CarryRoomAsync(string fromId, string toId, CancellationToken token = default) =>
		_held.CarryRoomAsync(fromId, toId, token);

	/// <inheritdoc/>
	public SaveOutcome LinkRoom(string fromId, string toId) => Written(_held.LinkRoom(fromId, toId));

	private static HeatingDocument? Restore(JsonNoteFile file, ILogger logger)
	{
		if (!file.TryRead(StoredSettings.SerializerOptions, out StoredSettings? stored, out bool fromBackup, out Exception? failure))
		{
			logger.LogWarning(failure, "Could not read {Path}, so the settings start again as unknown.", file.FilePath);
			Report(logger, file, "discarded what it found", "could not be read");

			return null;
		}

		if (stored is null)
		{
			Report(logger, file, "found nothing", "no file yet");

			return null;
		}

		if (stored.Version > StoredSettings.CurrentVersion)
		{
			logger.LogWarning(
				"{Path} was written by version {Found} of the settings and this build reads up to {Known}, so it is "
				+ "left alone and nothing is restored from it. Redeploying the newer build reads it again.",
				file.FilePath, stored.Version, StoredSettings.CurrentVersion);

			Report(logger, file, "discarded what it found", $"version {stored.Version} is newer than {StoredSettings.CurrentVersion}");

			return null;
		}

		Report(logger, file, fromBackup ? "restored from its backup" : "restored", $"saved {stored.SavedAt:u}");

		return stored.Settings;
	}

	private static void Report(ILogger logger, JsonNoteFile file, string outcome, string detail) =>
		logger.LogInformation("State at start: the settings {Outcome} from {Location} ({Detail}).", outcome, file.FilePath, detail);

	private SaveOutcome Written(SaveOutcome outcome) =>
		outcome.Written ? Save() : outcome;

	private SaveOutcome Save()
	{
		StoredSettings writing = new(StoredSettings.CurrentVersion, _clock.GetUtcNow(), _held.Read());

		lock (_writeGate)
		{
			if (!_file.TryWrite(writing, StoredSettings.SerializerOptions, out Exception? refused))
			{
				_logger.LogWarning(refused, "Could not write {Path}, so the save is refused rather than reported as kept.", FilePath);

				return SaveOutcome.Refused(refused.Message);
			}

			if (_file.TryRead(StoredSettings.SerializerOptions, out StoredSettings? back, out _, out Exception? unreadable)
				&& back is not null)
			{
				return SaveOutcome.Ok;
			}

			_logger.LogWarning(unreadable, "Wrote {Path} and could not read it back, so the save is refused.", FilePath);

			return SaveOutcome.Refused(unreadable?.Message ?? NotReadBack);
		}
	}
}
