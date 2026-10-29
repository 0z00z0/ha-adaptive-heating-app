using AdaptiveHeating.Planner;

using ChangeOrigin = AdaptiveHeating.Planner.ChangeOrigin;

namespace AdaptiveHeating.Page.Settings;

/// <summary>
/// Holds the settings for as long as the process runs, and is what every rule about them is written in. The
/// add-on wraps it in a file and in the calls to the thermostats; the render host uses it on its own.
/// </summary>
/// <remarks>The stamp rule lives here rather than in the page: an arriving number whose stamp is older than
/// the one held is refused and the refusal says so, exactly as a thermostat refuses one.</remarks>
public sealed class InMemorySettingsStore : ISettingsStore
{
	private readonly Lock _gate = new();

	private readonly Func<HeatingDocument, HeatingDocument>? _withTheValues;

	private HeatingDocument _document;

	/// <param name="withTheValues">Fills each room's mode temperatures from the settings document, or <c>null</c>
	/// where the caller hands them in itself.</param>
	public InMemorySettingsStore(HeatingDocument document, Func<HeatingDocument, HeatingDocument>? withTheValues = null)
	{
		_document = document ?? throw new ArgumentNullException(nameof(document));
		_withTheValues = withTheValues;
	}

	/// <inheritdoc/>
	public event Action? Changed;

	/// <inheritdoc/>
	public HeatingDocument Read()
	{
		HeatingDocument held;

		lock (_gate)
			held = _document;

		return _withTheValues is null ? held : _withTheValues(held);
	}

	/// <inheritdoc/>
	public void Receive(HeatingDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);

		lock (_gate)
			_document = document;

		Changed?.Invoke();
	}

	/// <inheritdoc/>
	public void ReceiveWhatTheRoomsAreDoing(
		InForceRecord inForce,
		IReadOnlyDictionary<string, RoomNow> now,
		IReadOnlyDictionary<string, RoomVerdict>? verdicts = null)
	{
		ArgumentNullException.ThrowIfNull(inForce);
		ArgumentNullException.ThrowIfNull(now);

		lock (_gate)
			_document = _document with { InForce = inForce, Now = now, Verdicts = verdicts ?? _document.Verdicts };

		Changed?.Invoke();
	}

	/// <inheritdoc/>
	public void ReceiveTheCalendarsOffered(IReadOnlyList<CalendarOnOffer> calendars)
	{
		ArgumentNullException.ThrowIfNull(calendars);

		lock (_gate)
		{
			// Compared before it is stored: this arrives on every pass and raising the change would redraw every
			// open page once a minute.
			if (_document.CalendarsOffered.SequenceEqual(calendars))
				return;

			_document = _document with { CalendarsOffered = [.. calendars] };
		}

		Changed?.Invoke();
	}

	/// <inheritdoc/>
	public SaveOutcome SaveCalendars(string? arrivals, string? record)
	{
		lock (_gate)
		{
			_document = _document with
			{
				ArrivalCalendar = Named(arrivals),
				RecordCalendar = Named(record)
			};
		}

		Changed?.Invoke();

		return SaveOutcome.Ok;
	}

	/// <inheritdoc/>
	public SaveOutcome SaveModeStamps(string roomId, IReadOnlyCollection<HeatingMode> modes, Stamp stamp)
	{
		ArgumentNullException.ThrowIfNull(modes);

		return Change(roomId, room =>
		{
			Dictionary<HeatingMode, ModeStamp> held = new(room.ModeStamps);
			ModeStamp arriving = new(stamp, ChangeOrigin.SettingsPage);

			foreach (HeatingMode mode in modes)
			{
				if (held.TryGetValue(mode, out ModeStamp already) && !already.IsReplacedBy(arriving))
					return (room, SaveOutcome.Refused(RefusedForAnOlderStamp));

				held[mode] = arriving;
			}

			return (room with { ModeStamps = held }, SaveOutcome.Ok);
		});
	}

	/// <inheritdoc/>
	public SaveOutcome SaveSensorSettings(string roomId, RoomSensorSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);

		// A room whose own reading is borrowed cannot lend, so a lender that starts borrowing is refused here
		// rather than leaving every borrowing room to discover it.
		if (settings.BorrowsFrom is { Length: > 0 } lender)
		{
			HeatingDocument document = Read();
			RoomSlice? lending = document.RoomIn(lender);

			if (lending is not null && string.Equals(lending.Id, roomId, StringComparison.Ordinal))
				return SaveOutcome.Refused("A room cannot borrow from itself.");

			if (lending is { Borrows: true })
				return SaveOutcome.Refused("That room borrows its own reading.");
		}

		return Change(roomId, room => (room with { Sensors = settings }, SaveOutcome.Ok));
	}

	/// <inheritdoc/>
	public SaveOutcome SaveModeSwitch(string roomId, HeatingMode mode, bool on, Stamp stamp) =>
		Change(roomId, room =>
		{
			Stamped<bool> arriving = new(on, stamp, ChangeOrigin.SettingsPage);

			if (room.ModeSwitches.TryGetValue(mode, out Stamped<bool> held) && !held.IsReplacedBy(arriving))
				return (room, SaveOutcome.Refused(RefusedForAnOlderStamp));

			Dictionary<HeatingMode, Stamped<bool>> switches = new(room.ModeSwitches) { [mode] = arriving };

			return (room with { ModeSwitches = switches }, SaveOutcome.Ok);
		});

	/// <inheritdoc/>
	public SaveOutcome ResetLearnt(string roomId, LearntKind kind) =>
		Change(roomId, room => (room with { Learnt = Replace(room.Learnt, kind, Reset) }, SaveOutcome.Ok));

	/// <inheritdoc/>
	public SaveOutcome SetLearntByHand(string roomId, LearntKind kind, double? value) =>
		Change(roomId, room =>
			(room with { Learnt = Replace(room.Learnt, kind, held => held with { ValueSetByAPerson = value }) }, SaveOutcome.Ok));

	/// <inheritdoc/>
	// No thermostat to move: the settings alone have nothing to call.
	public Task<SaveOutcome> CarryRoomAsync(string fromId, string toId, CancellationToken token = default) =>
		Task.FromResult(SaveOutcome.Ok);

	/// <inheritdoc/>
	public SaveOutcome LinkRoom(string fromId, string toId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fromId);
		ArgumentException.ThrowIfNullOrWhiteSpace(toId);

		lock (_gate)
		{
			// A room the integration has not named yet has nothing here to link.
			if (_document.RoomIn(fromId) is not { } moving)
				return SaveOutcome.Ok;

			_document = _document with
			{
				Rooms =
				[
					.. _document.Rooms.Where(room => !ReferenceEquals(room, moving) &&!string.Equals(room.AreaId, toId, StringComparison.Ordinal)),
					moving with { AreaId = toId }
				]
			};
		}

		Changed?.Invoke();

		return SaveOutcome.Ok;
	}

	private const string RefusedForAnOlderStamp = "Changed elsewhere since this page loaded.";

	// A blank choice and no choice are the same thing, so the field never holds whitespace a comparison would miss.
	private static string? Named(string? entityId) =>
		entityId?.Trim() is { Length: > 0 } named ? named : null;

	// The measurements go with the reset, so clearing a lock afterwards hands back the starting value rather
	// than a number fitted to a room that has since been rebuilt.
	private static LearntNumber Reset(LearntNumber held) =>
		held with { SystemValue = held.StartingValue, MeasurementCount = 0, AgreementsSoFar = 0, LastChangedAt = null };

	private static RoomLearntNumbers Replace(RoomLearntNumbers held, LearntKind kind, Func<LearntNumber, LearntNumber> change) =>
		kind switch
		{
			LearntKind.WarmingRate => held with { WarmingRate = change(held.WarmingRate) },
			LearntKind.OutdoorTerm => held with { OutdoorTerm = change(held.OutdoorTerm) },
			_ => held with { Band = change(held.Band) }
		};

	private SaveOutcome Change(string roomId, Func<RoomSlice, (RoomSlice Room, SaveOutcome Outcome)> change)
	{
		SaveOutcome outcome;

		lock (_gate)
		{
			int at = -1;

			for (int index = 0; index < _document.Rooms.Count; index++)
			{
				if (string.Equals(_document.Rooms[index].Id, roomId, StringComparison.Ordinal))
				{
					at = index;
					break;
				}
			}

			if (at < 0)
				return SaveOutcome.Refused("That room is no longer set up.");

			(RoomSlice changed, outcome) = change(_document.Rooms[at]);

			if (!outcome.Written)
				return outcome;

			List<RoomSlice> rooms = [.. _document.Rooms];
			rooms[at] = changed;

			_document = _document with { Rooms = rooms };
		}

		Changed?.Invoke();

		return outcome;
	}
}
