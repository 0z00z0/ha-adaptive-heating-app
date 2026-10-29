using System.Collections.Concurrent;
using System.Globalization;

using Adaptive.Settings;

using AdaptiveHeating.AddOn.Integration;
using AdaptiveHeating.AddOn.Reporting;
using AdaptiveHeating.AddOn.Settings;
using AdaptiveHeating.Page.Settings;
using AdaptiveHeating.Planner;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Engine;

using ChangeOrigin = AdaptiveHeating.Planner.ChangeOrigin;

namespace AdaptiveHeating.AddOn.Driving;

/// <summary>What one pass decided, for the record and for a test to read.</summary>
/// <param name="Outdoor">The outdoor temperature the pass ran on, and where it came from.</param>
/// <param name="NextArrival">The arrival this pass planned against, or <c>null</c> where none is ahead.</param>
/// <param name="Recorded">How many finished warm-ups reached the record calendar on this pass.</param>
internal sealed record DrivingPass(
	HeatingMode Mode,
	HouseState House,
	int RoomsDriven,
	int Sent,
	OutdoorReading Outdoor,
	DateTimeOffset? NextArrival,
	int Recorded);

/// <summary>
/// One pass over the rooms: the mode in force resolved to an instruction per room, any warm-up that is due,
/// and what each room's own behaviour teaches.
/// </summary>
/// <remarks>
///     Nothing here decides a rule. The mode table, the precedence order, the warm-up
///     arithmetic and the two learning watchers all live in the planner; this reads the thermostats, hands the
///     planner numbers, and calls the actions its answers name.
/// </remarks>
internal sealed class DrivesTheRooms
{
	/// <summary>How far two temperatures may sit apart and still count as the same one. Half the thermostat's step.</summary>
	private const double SameTemperature = 0.25;

	/// <summary>How many rises one room keeps. A winter of them fits, and nothing reads the ones beyond it.</summary>
	private const int RisesKept = 400;

	/// <summary>What holds every room while the house is in a guest mode.</summary>
	internal const string GuestHold = "guest mode";

	private readonly ISettingsStore _store;
	private readonly Thermostats _thermostats;
	private readonly CoreApi _api;
	private readonly PlannerDefaults _defaults;
	private readonly TheHouseholdClock _household;
	private readonly Func<HeatingConfig> _document;
	private readonly ChangesAtTheThermostats _changes;
	private readonly ReportsWhatHappened _reports;
	private readonly TheArrivals _arrivals;
	private readonly RecordsTheWarmUps _record;
	private readonly TheHouseMode _houseMode;
	private readonly RoomHistory _history;
	private readonly IEntityLastSeen? _lastSeen;
	private readonly IStatePublisher? _publisher;
	private readonly ILogger _logger;
	private readonly Dictionary<string, RoomDriving> _driving = new(StringComparer.Ordinal);

	private HeatingMode? _modeReported;
	private bool _pausedLogged;
	private bool _reportedTheSensorsOut;
	private bool _reportedTheZone;
	private InForceRecord _inForce = InForceRecord.Empty;

	// What the last pass saw, so a period entering or the house mode moving is told from a steady pass.
	private GlobalConfig? _seenUnder;
	private string? _periodSeen;
	private ModeKind _kindSeen;
	private string? _modeValueSeen;

	// Lighting's keyed-by-value warning: once per preset name for the run.
	private readonly ConcurrentDictionary<string, byte> _unknownPresets = new(StringComparer.OrdinalIgnoreCase);

	/// <param name="changes">Who changed each thermostat, sorted as the change arrived.</param>
	public DrivesTheRooms(
		ISettingsStore store,
		Thermostats thermostats,
		CoreApi api,
		PlannerDefaults defaults,
		TheHouseholdClock household,
		Func<HeatingConfig> document,
		ChangesAtTheThermostats changes,
		ReportsWhatHappened reports,
		TheArrivals arrivals,
		RecordsTheWarmUps record,
		TheHouseMode houseMode,
		ILogger<DrivesTheRooms> logger,
		RoomHistory? history = null,
		IEntityLastSeen? lastSeen = null,
		IStatePublisher? publisher = null)
	{
		_publisher = publisher;
		_store = store ?? throw new ArgumentNullException(nameof(store));
		_thermostats = thermostats ?? throw new ArgumentNullException(nameof(thermostats));
		_api = api ?? throw new ArgumentNullException(nameof(api));
		_defaults = defaults ?? throw new ArgumentNullException(nameof(defaults));
		_household = household ?? throw new ArgumentNullException(nameof(household));
		_document = document ?? throw new ArgumentNullException(nameof(document));
		_changes = changes ?? throw new ArgumentNullException(nameof(changes));
		_reports = reports ?? throw new ArgumentNullException(nameof(reports));
		_arrivals = arrivals ?? throw new ArgumentNullException(nameof(arrivals));
		_record = record ?? throw new ArgumentNullException(nameof(record));
		_houseMode = houseMode ?? throw new ArgumentNullException(nameof(houseMode));
		_history = history ?? new RoomHistory(store: null);
		_lastSeen = lastSeen;
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <summary>What each room is being told to hold, which is what the settings page states beside a room's settings.</summary>
	public InForceRecord InForce => _inForce;

	private GlobalConfig House => _document().Global;

	public async Task<DrivingPass> RunAsync(IReadOnlyList<EntityState> states, DateTimeOffset now, CancellationToken token)
	{
		ArgumentNullException.ThrowIfNull(states);

		HeatingDocument document = _store.Read();
		(HouseState house, DateTimeOffset? modeSince) = _houseMode.Now();
		HeatingMode mode = TheHouseMode.HeatingModeFor(house.ActiveKind);

		if (_modeReported != mode)
		{
			_modeReported = mode;

			await _reports
				.ReportAsync(HeatingReport.About(WhatHappened.ModeInForce, now) with { Named = mode.ToString() }, token)
				.ConfigureAwait(false);
		}

		await ReportTheHouseholdClockAsync(now, token).ConfigureAwait(false);

		OutdoorReading outdoor = await TheOutdoorTemperature
			.ReadAsync(states, OutdoorChoice.From(House), House.WarmUpSource, _api, now, _defaults, token, _lastSeen)
			.ConfigureAwait(false);

		await ReportTheOutdoorSensorsAsync(outdoor, now, token).ConfigureAwait(false);

		Dictionary<string, ThermostatNow> reporting = new(StringComparer.Ordinal);

		foreach (EntityState state in states)
		{
			if (ThermostatNow.From(state, now) is { } thermostat)
				reporting[thermostat.RoomId] = thermostat;
		}

		// Read before the rooms are driven, so a room reached on this pass is planned against the same arrival as
		// every other. A reading is taken again on its own cadence and stands between.
		DateTimeOffset? arrival = await _arrivals.NextAsync(document, states, now, token).ConfigureAwait(false);

		List<RoomSlice> rooms = [];
		Dictionary<string, Stamped<double>> told = new(StringComparer.Ordinal);
		Dictionary<string, RoomVerdict> verdicts = new(StringComparer.Ordinal);
		HeatingConfig config = _document();
		Dictionary<string, EntityState> byId = new(StringComparer.Ordinal);

		foreach (EntityState state in states)
			byId[state.EntityId] = state;
		bool moved = false;
		int driven = 0;
		int sent = 0;

		LogThePause(house.KillSwitchActive);

		// The only moments a preset is written: between them a card's own choice holds its room.
		bool boundary = ABoundaryWentBy(document.PeriodKeyInForce, house);

		if (boundary)
			_logger.LogInformation("A new period or house mode: every room's card is set to '{Preset}'.", RoomPresets.For(house.ActiveKind));

		foreach (RoomSlice room in document.Rooms)
		{
			AreaConfig? area = config.Areas.FirstOrDefault(candidate => string.Equals(candidate.AreaId, room.AreaId, StringComparison.Ordinal));
			RoomGates gates = RoomGates.Of(area, config, byId);

			// Lighting's one order. The master switch and a disabled room stop every command; the rooms are still read.
			HouseGate gate = HouseGates.FirstClosed(
				new HouseGateState(house.KillSwitchActive, gates.Enabled, house.ActiveKind, () => gates.PausedBy),
				HouseGate.KillSwitch,
				HouseGate.EntityOn);

			// A guest mode holds the room as a hold-while entity does; a paused room is heated as in Away.
			RoomGates applied = gate is HouseGate.SceneHold ? gates with { HeldBy = gates.HeldBy ?? GuestHold, PausedBy = null } : gates;

			RoomDriving driving = DrivingOf(room.Id);

			// The add-on's last command from the note, so its own write is not read as a hand after a restart.
			if (driving.LastCommanded is null && room.Commanded is { } last)
			{
				driving.LastCommanded = last;
				driving.CommandedAt = room.CommandedAt;
			}

			(ModeKind kind, DateTimeOffset? since) = ModeOf(reporting.GetValueOrDefault(room.Id), house, modeSince, boundary);
			HeatingMode roomMode = TheHouseMode.HeatingModeFor(kind);

			if (gate is HouseGate.KillSwitch or HouseGate.Disabled
				|| room.Thermostat is not { Length: > 0 }
				|| !reporting.TryGetValue(room.Id, out ThermostatNow? thermostat))
			{
				verdicts[room.Id] = VerdictOf(room, applied, house, driving);
				rooms.Add(room);
				Publish(room, reporting.GetValueOrDefault(room.Id), gate, applied, house, roomMode, document.PeriodInForce, document.PeriodKeyInForce, byId, now);
				continue;
			}

			if (boundary && thermostat.TakesAPreset)
				sent += await WriteThePresetAsync(room, house.ActiveKind, token).ConfigureAwait(false);

			driven++;
			RoomOutcome outcome = await DriveAsync(
					room, thermostat, gate is HouseGate.EntityOn ? HeatingMode.Away : roomMode, applied, since, now, outdoor, arrival, told,
					new Tonight(area, area?.Effective(config.Defaults) ?? config.Defaults, document, gate is HouseGate.EntityOn), token)
				.ConfigureAwait(false);
			verdicts[room.Id] = VerdictOf(room, applied, house, driving);
			rooms.Add(outcome.Room);
			Publish(room, thermostat, gate, applied, house, roomMode, document.PeriodInForce, document.PeriodKeyInForce, byId, now);
			moved |= outcome.Moved;
			sent += outcome.Sent;
		}

		if (moved)
			_store.Receive(document with { Rooms = rooms });

		_history.KeepOnly([.. document.Rooms.Select(room => room.Id)]);

		KeepWhatIsInForce(mode, modeSince, told, now);

		_store.ReceiveWhatTheRoomsAreDoing(_inForce, ReadingsFrom(reporting, now), verdicts);

		// After the rooms, so a warm-up whose deadline fell during this pass is written down with what its room
		// reads now rather than with what it read a minute ago.
		int recorded = await _record
			.WriteDownWhatFinishedAsync(document, states, reporting, now, token)
			.ConfigureAwait(false);

		return new DrivingPass(mode, house, driven, sent, outdoor, arrival, recorded);
	}

	private RoomVerdict VerdictOf(RoomSlice room, RoomGates gates, HouseState house, RoomDriving driving) =>
		gates.Verdict with
		{
			MasterSwitchOff = house.KillSwitchActive,
			History = Shown(_history.For(room.Id)),
			CommandedAt = driving.CommandedAt,
			NightSetback = driving.SetbackWords,
			SetbackThermostatFloor = driving.SetbackThermostatFloor
		};

	// A person is kept by user id and an automation by entity id, and named here, when shown, so a rename reads as the new name.
	private AreaHistory? Shown(AreaHistory? history) => history switch
	{
		{ ChangedByUserId: { Length: > 0 } userId } => history with { ChangedBy = _changes.PersonWords(userId) },
		{ ChangedByAutomationId: { Length: > 0 } automation } => history with { ChangedBy = _changes.AutomationWords(automation) },
		_ => history
	};

	/// <summary>Lighting's room event: fired when what the room is doing has changed, never on a steady pass.</summary>
	private void Publish(
		RoomSlice room,
		ThermostatNow? thermostat,
		HouseGate gate,
		RoomGates gates,
		HouseState house,
		HeatingMode roomMode,
		string? period,
		string? periodId,
		IReadOnlyDictionary<string, EntityState> states,
		DateTimeOffset now)
	{
		if (_publisher is null)
			return;

		RoomDriving driving = DrivingOf(room.Id);
		AreaState state = StateOf(thermostat, gate, gates, roomMode, driving, now);
		AreaHistory? history = Shown(_history.For(room.Id));

		AreaSnapshot snapshot = new(
			room.Name,
			state,
			TransitionReason.CircadianTick,
			house.ActiveKind,
			house.KillSwitchActive,
			period,
			thermostat?.Target,
			thermostat?.RoomTemperature,
			now,
			driving.CommandedAt,
			driving.HandChange is { } hand ? hand.Stamp.SetAt + gates.HandHoldsFor : null,
			driving.HandChange?.Stamp.SetAt,
			house.ModeValue,
			room.AreaId,
			gate switch
			{
				HouseGate.KillSwitch => AutoOnBlock.KillSwitch,
				HouseGate.Disabled => AutoOnBlock.Disabled,
				HouseGate.SceneHold => AutoOnBlock.SceneHold,
				HouseGate.EntityOn => AutoOnBlock.EntityOn,
				_ => AutoOnBlock.None
			},
			gates.PausedBy,
			room.LevelsFromRoom,
			house.IsAnyoneHome,
			house.Forced,
			gates.HeldBy is not null,
			gates.HeldBy,
			driving.LastSource?.ToString(),
			thermostat?.HeatersNotResponding,
			thermostat?.HeaterCount,
			history?.ChangedBy,
			history?.ChangedAt,
			LowBatteries(room, states),
			house.ActiveModeName,
			room.Id,
			periodId,
			history?.ChangedByUserId);

		if (snapshot.HasSameMeaningAs(driving.Published))
			return;

		snapshot = snapshot with { Reason = ReasonFor(driving.Published, snapshot) };
		driving.Published = snapshot;
		_publisher.Publish(snapshot);
	}

	private static AreaState StateOf(
		ThermostatNow? thermostat,
		HouseGate gate,
		RoomGates gates,
		HeatingMode roomMode,
		RoomDriving driving,
		DateTimeOffset now) =>
		gate switch
		{
			HouseGate.KillSwitch or HouseGate.Disabled => AreaState.Disabled,
			_ when thermostat is null => AreaState.NotReporting,
			HouseGate.SceneHold => AreaState.SceneHold,
			_ when gates.HeldBy is not null => AreaState.SceneHold,
			HouseGate.EntityOn => AreaState.Away,
			_ when thermostat.WarmUpRunning || thermostat.HoldUnexpiredAt(now) => AreaState.WarmingUp,
			_ when driving.HandChange is not null => AreaState.OverriddenOn,
			_ when roomMode is HeatingMode.Away => AreaState.Away,
			_ => AreaState.AutoActive
		};

	// What moved since the room's last event, in lighting's order of what matters most.
	private static TransitionReason ReasonFor(AreaSnapshot? before, AreaSnapshot now) =>
		before switch
		{
			null => TransitionReason.Startup,
			_ when (before.State is AreaState.Disabled) != (now.State is AreaState.Disabled) => TransitionReason.EnablementChanged,
			_ when before.Mode != now.Mode || before.HouseModeValue != now.HouseModeValue => TransitionReason.HouseModeChanged,
			_ when now.State is AreaState.OverriddenOn && before.State is not AreaState.OverriddenOn => TransitionReason.ManualOn,
			_ when before.State is AreaState.OverriddenOn && now.State is not AreaState.OverriddenOn => TransitionReason.OverrideExpired,
			_ when before.IsHeld != now.IsHeld => TransitionReason.SceneHold,
			_ when !(before.LowBatteries ?? []).SequenceEqual(now.LowBatteries ?? []) => TransitionReason.SensorBattery,
			_ => TransitionReason.CircadianTick
		};

	private static List<SensorBattery> LowBatteries(RoomSlice room, IReadOnlyDictionary<string, EntityState> states)
	{
		return
		[
			.. (room.Batteries ?? [])
				.Select(battery => battery.LowFrom(Read(battery.LowEntity), Read(battery.LevelEntity)))
				.OfType<SensorBattery>()
		];

		string? Read(string? entityId) =>
			entityId is not null && states.TryGetValue(entityId, out EntityState? state) ? state.State : null;
	}

	// Lighting's period entry, edge-triggered on the period's key with both known, or the house mode moving. The first
	// pass and the first after a save see no boundary: a save rebuilds the rules and is never a boundary that went by.
	private bool ABoundaryWentBy(string? period, HouseState house)
	{
		GlobalConfig global = House;
		bool steady = _seenUnder is not null && ReferenceEquals(_seenUnder, global);

		bool entered = period is { Length: > 0 }
			&& _periodSeen is { Length: > 0 }
			&& !string.Equals(period, _periodSeen, StringComparison.OrdinalIgnoreCase);
		bool houseMoved = house.ActiveKind != _kindSeen || !string.Equals(house.ModeValue, _modeValueSeen, StringComparison.Ordinal);

		_seenUnder = global;
		_periodSeen = period;
		_kindSeen = house.ActiveKind;
		_modeValueSeen = house.ModeValue;

		return steady && (entered || houseMoved);
	}

	/// <summary>The room's mode: the preset on its own card, else the house's; and when that mode came into force.</summary>
	private (ModeKind Kind, DateTimeOffset? Since) ModeOf(
		ThermostatNow? thermostat,
		HouseState house,
		DateTimeOffset? modeSince,
		bool boundary)
	{
		// A boundary releases every held preset, so this pass runs the house's mode before the write echoes back.
		if (boundary || thermostat?.Preset is not { } preset || preset == RoomPresets.None)
			return (house.ActiveKind, modeSince);

		if (RoomPresets.KindOf(preset) is not { } kind)
		{
			if (_unknownPresets.TryAdd(preset, 0))
				_logger.LogWarning(
					"Thermostat {Entity} holds preset '{Value}', which the integration's preset list does not hold; the room follows the house mode.",
					thermostat.EntityId, preset);

			return (house.ActiveKind, modeSince);
		}

		// A preset chosen on the card is a change at the room, stamped when it was chosen.
		return (kind, thermostat.PresetSetAt is { } chosen && (modeSince is null || chosen > modeSince) ? chosen : modeSince);
	}

	/// <summary>The house's preset on a room's card, which releases whatever the card held.</summary>
	private async Task<int> WriteThePresetAsync(RoomSlice room, ModeKind kind, CancellationToken token)
	{
		ActionOutcome outcome = await _thermostats
			.SetPresetAsync(room.Thermostat!, RoomPresets.For(kind), token)
			.ConfigureAwait(false);

		return outcome == ActionOutcome.Called ? 1 : 0;
	}

	private void LogThePause(bool paused)
	{
		if (paused == _pausedLogged)
			return;

		_pausedLogged = paused;

		if (paused)
			_logger.LogInformation("The master switch pauses the heating: no thermostat is commanded until it is turned back on.");
		else
			_logger.LogInformation("The master switch lets the heating run again.");
	}

	private static Dictionary<string, RoomNow> ReadingsFrom(
		Dictionary<string, ThermostatNow> reporting,
		DateTimeOffset readAt)
	{
		Dictionary<string, RoomNow> readings = new(StringComparer.Ordinal);

		foreach (ThermostatNow thermostat in reporting.Values)
		{
			// A room publishing no target has no temperature line to show, and a zero here would read as one.
			if (thermostat.Target is not { } target)
				continue;

			readings[thermostat.RoomId] = new RoomNow(
				thermostat.RoomId,
				thermostat.RoomTemperature,
				target,
				thermostat.HeaterCommandedOn,
				readAt);
		}

		return readings;
	}

	/// <summary>Merges this pass's instructions into the record of what is in force, and says what it refused.</summary>
	private void KeepWhatIsInForce(
		HeatingMode mode,
		DateTimeOffset? modeSince,
		Dictionary<string, Stamped<double>> told,
		DateTimeOffset now)
	{
		_inForce = _inForce with
		{
			Mode = new Stamped<string>(mode.ToString(), Stamp.Certain(modeSince ?? now), ChangeOrigin.SettingsPage)
		};

		if (told.Count == 0)
			return;

		MergeOutcome merged = _inForce.Merge(told.Select(one => new ArrivingTemperature(one.Key, one.Value)));
		_inForce = merged.Record;

		foreach (RefusedChange refused in merged.Refused)
		{
			_logger.LogInformation(
				"{Room} keeps {Held} °C in force: {Arriving} °C was set earlier than that.",
				refused.RoomId, refused.TemperatureHeld, refused.ArrivingTemperature);
		}
	}

	/// <summary>A fresh connection to Home Assistant, after which whatever still holds is said again.</summary>
	/// <remarks>
	///     Home Assistant holds no cards after a core restart, and this side cannot see what it is still showing.
	///     Every card carries a stable id, so re-raising one replaces its own card rather than stacking a second.
	/// </remarks>
	public void TheConnectionCameBack()
	{
		_reportedTheSensorsOut = false;
		_arrivals.TheConnectionCameBack();
	}

	// Said once while it stays true, and again the next time it becomes true: a cabin whose outdoor sensors are
	// all dead would otherwise raise the same card every minute.
	/// <summary>Raises the card for a household zone that did not resolve, which nothing here can put right.</summary>
	private async Task ReportTheHouseholdClockAsync(DateTimeOffset now, CancellationToken token)
	{
		if (_household.Resolved || _reportedTheZone)
			return;

		// Latched on the card arriving, never on the attempt. A card refused for want of a connection would
		// otherwise leave a household whose every wall time is wrong with no card at all.
		_reportedTheZone = await _reports
			.ReportAsync(
				HeatingReport.About(WhatHappened.HouseholdZoneNotResolved, now) with { Named = _household.Named },
				token)
			.ConfigureAwait(false);
	}

	private async Task ReportTheOutdoorSensorsAsync(OutdoorReading outdoor, DateTimeOffset now, CancellationToken token)
	{
		bool allOut = outdoor.From is OutdoorFrom.Forecast or OutdoorFrom.ForecastAhead or OutdoorFrom.Nothing;

		if (!allOut)
		{
			_reportedTheSensorsOut = false;

			return;
		}

		if (_reportedTheSensorsOut)
			return;

		// Latched on the card arriving, never on the attempt. A card refused for want of a connection would
		// otherwise leave a cabin whose sensors were already out with no card at all.
		_reportedTheSensorsOut = await _reports
			.ReportAsync(
				HeatingReport.About(WhatHappened.OutdoorSensorsAllFailed, now) with
				{
					Named = outdoor.From is OutdoorFrom.Forecast or OutdoorFrom.ForecastAhead ? House.ForecastEntity : null,
					Against = outdoor.Temperature
				},
				token)
			.ConfigureAwait(false);
	}

	private sealed record RoomOutcome(RoomSlice Room, bool Moved, int Sent);

	/// <summary>What the night setback reads beyond the room itself.</summary>
	private sealed record Tonight(AreaConfig? Area, AreaSettings Settings, HeatingDocument Document, bool Paused);

	private async Task<RoomOutcome> DriveAsync(
		RoomSlice room,
		ThermostatNow thermostat,
		HeatingMode mode,
		RoomGates gates,
		DateTimeOffset? modeSince,
		DateTimeOffset now,
		OutdoorReading outdoor,
		DateTimeOffset? arrival,
		Dictionary<string, Stamped<double>> told,
		Tonight tonight,
		CancellationToken token)
	{
		RoomDriving driving = DrivingOf(room.Id);
		Stamped<RoomInstruction>? asked = ModeTable.For(mode, !room.NoReadingAtAll, room.ModeTemperatures, room.ModeSwitches);

		// A held room keeps what it switched last; nothing new is asked of it until the hold lets go.
		if (room.NoReadingAtAll)
			return new RoomOutcome(
				room,
				false,
				gates.HeldBy is null ? await SwitchAsync(room, driving, asked, now, token).ConfigureAwait(false) : 0);

		int sent = 0;
		RoomSlice before = room;
		double? outside = outdoor.From == OutdoorFrom.NothingChosen ? thermostat.Outdoor : outdoor.Temperature;

		SetbackVerdict setback = mode is HeatingMode.Night && asked is { } night
			? NightSetback.Plan(SetbackInputs(room, thermostat, gates, tonight, night.Value.Temperature, outside, now), _defaults)
			: SetbackVerdict.None(NoSetback.NotThisMode);
		bool wasSetBack = driving.SetbackTemperature is not null;

		// The setback keeps the night temperature's stamp, so a hand change wins as before; the record's entry is let
		// go where the number moves, or its own stamp would refuse the new one.
		if (driving.SetbackTemperature != setback.Plan?.Temperature)
		{
			driving.SetbackTemperature = setback.Plan?.Temperature;
			Release(room.Id);
		}

		driving.SetbackWords = tonight.Area?.NightSetback is true ? SetbackWords(setback, now) : null;
		driving.SetbackThermostatFloor = tonight.Area?.NightSetback is true ? setback.ThermostatFloor : null;

		if (setback.Plan is { } plan && asked is { } nightly)
			asked = new Stamped<RoomInstruction>(RoomInstruction.Holds(plan.Temperature), nightly.Stamp, nightly.Origin);

		if (asked is { } instruction)
			sent += await HoldAsync(room, thermostat, driving, instruction, gates, modeSince, now, told, token).ConfigureAwait(false);

		if (room.CommandedAt != driving.CommandedAt)
			room = room with { Commanded = driving.LastCommanded, CommandedAt = driving.CommandedAt };

		RoomSlice after = await LearnAsync(room, thermostat, driving, now, outside, token).ConfigureAwait(false);

		// Whatever mode is in force: an arrival is people coming to a cabin that has been standing cold, and the
		// mode that will be in force when they are there is the one the room is warmed to.
		if (arrival is { } deadline)
			sent += await WarmUpForTheArrivalAsync(after, thermostat, driving, deadline, after.TemperatureFor(HeatingMode.Home), now, outside, token).ConfigureAwait(false);

		// The restart is the arrival's own warm-up, aimed at the next period's start.
		if ((setback.Plan is not null || (wasSetBack && setback.Reason is NoSetback.TooLittleTime or NoSetback.TooLittleToGain))
			&& tonight.Document.NextPeriodStartsAt is { } periodStart
			&& after.NextPeriodTemperature is { } periodTemperature)
			sent += await WarmUpForTheArrivalAsync(after, thermostat, driving, periodStart, periodTemperature, now, outside, token).ConfigureAwait(false);

		return new RoomOutcome(after, !ReferenceEquals(after, before), sent);
	}

	private NightSetbackInputs SetbackInputs(
		RoomSlice room,
		ThermostatNow thermostat,
		RoomGates gates,
		Tonight tonight,
		double night,
		double? outside,
		DateTimeOffset now) =>
		new()
		{
			SetbackOnForTheRoom = tonight.Area?.NightSetback is true,
			MarkedModeInForce = tonight.Document.NightSetbackInForce,
			RoomEnabled = gates.Enabled,
			Paused = tonight.Paused,
			Held = gates.HeldBy is not null,
			HasOwnSensor = !room.NoReadingAtAll && !room.Borrows && tonight.Area?.Neighbour is not { Length: > 0 },
			HasAPowerMeter = thermostat.HasAPowerMeter,
			FaultReported = thermostat.ReportsAFault,
			RoomTemperature = thermostat.RoomTemperature,
			NightTemperature = night,
			PeriodTemperature = room.NextPeriodTemperature,
			LowestNightTemperature = tonight.Settings.LowestNightTemperature,
			FrostLimit = tonight.Settings.FrostProtection ? tonight.Settings.FrostTemperature : null,
			ThermostatMinimum = thermostat.ThermostatMinimum,
			WarmingRate = room.RateMeasurements.Count > 0 ? Estimate(room, outside, now).DegreesPerHour : null,
			OutdoorTemperature = outside,
			HeatLoss = HeatLoss.Estimate(room.HeatLossMeasurements, now, _defaults),
			UntilThePeriodStarts = tonight.Document.NextPeriodStartsAt - now,
		};

	private string SetbackWords(SetbackVerdict setback, DateTimeOffset now) =>
		setback.Plan is { } plan
			? string.Create(
				CultureInfo.CurrentCulture,
				$"{plan.Temperature:0.#} °C{(setback.ThermostatFloor is { } floor && plan.Temperature - floor < _defaults.SetbackStep ? ", thermostat minimum" : "")}, heats from {TimeZoneInfo.ConvertTime(now + plan.RestartsAfter, _household.Zone):HH:mm}")
			: setback.Reason switch
			{
				NoSetback.NotThisMode => "Waiting for the night mode",
				NoSetback.MasterSwitchOff => "Master switch off",
				NoSetback.RoomSwitchedOff => "Room switched off",
				NoSetback.Paused => "Paused",
				NoSetback.Held => "Held",
				NoSetback.NoSensor => "No sensor of its own",
				NoSetback.NoReading => "No reading",
				NoSetback.NoPowerMeter => "No power meter",
				NoSetback.Fault => "Fault reported",
				NoSetback.RateNotLearnt => "Warming rate not learnt yet",
				NoSetback.LossNotLearnt => "Heat loss not learnt yet",
				NoSetback.NoOutdoorFigure => "No outdoor temperature",
				NoSetback.HeaterTooSmall => "Heater too small",
				NoSetback.NoPeriodAhead => "No next period",
				NoSetback.TooLittleTime => "Too close to the next period",
				_ => "Nothing to gain now"
			};

	private void Release(string roomId) =>
		_inForce = _inForce with
		{
			Temperatures = _inForce.Temperatures
				.Where(entry => !string.Equals(entry.Key, roomId, StringComparison.Ordinal))
				.ToDictionary(StringComparer.Ordinal)
		};

	/// <summary>A room with no reading is switched on or off, and nothing else is done to it.</summary>
	private async Task<int> SwitchAsync(
		RoomSlice room,
		RoomDriving driving,
		Stamped<RoomInstruction>? asked,
		DateTimeOffset now,
		CancellationToken token)
	{
		if (asked is not { } instruction || driving.LastSwitched == instruction.Value.SwitchedOn)
			return 0;

		ActionOutcome outcome = await _thermostats
			.SwitchAsync(room.Thermostat!, instruction.Value.SwitchedOn, token)
			.ConfigureAwait(false);

		if (outcome != ActionOutcome.Called)
			return 0;

		driving.LastSwitched = instruction.Value.SwitchedOn;

		await _reports
			.ReportAsync(
				HeatingReport.About(WhatHappened.RoomSwitched, now, room.Id) with { SwitchedOn = instruction.Value.SwitchedOn },
				token)
			.ConfigureAwait(false);

		return 1;
	}

	/// <summary>One temperature per room, out of the mode, a hand change and whatever the room is running.</summary>
	private async Task<int> HoldAsync(
		RoomSlice room,
		ThermostatNow thermostat,
		RoomDriving driving,
		Stamped<RoomInstruction> asked,
		RoomGates gates,
		DateTimeOffset? modeSince,
		DateTimeOffset now,
		Dictionary<string, Stamped<double>> told,
		CancellationToken token)
	{
		// A room publishing no target holds nothing to weigh a candidate against, and takes on or off instead.
		if (thermostat.Target is not { } holding)
			return 0;

		if (NoteAHandChange(thermostat, holding, asked.Value.Temperature, driving, gates.AutomationsCountAsHand, now) is { } noted)
		{
			await _reports
				.ReportAsync(
					HeatingReport.About(WhatHappened.HandChange, noted.Stamp.SetAt, room.Id) with
					{
						Temperature = noted.Value,
						Named = noted.By.Words,
						ChangedByUserId = noted.By.UserId,
						ChangedByAutomationId = noted.By.AutomationId
					},
					token)
				.ConfigureAwait(false);
		}

		// A change at the thermostat holds the room for the room's hold length, then the mode takes it back.
		if (driving.HandChange is { } hand && now - hand.Stamp.SetAt >= gates.HandHoldsFor)
		{
			driving.HandChange = null;

			// The record holds the hand change's newer stamp, and would refuse the older mode taking the room back.
			Release(room.Id);

			await _reports
				.ReportAsync(HeatingReport.About(WhatHappened.HandChangeEnded, now, room.Id), token)
				.ConfigureAwait(false);
		}

		RoomTargetCandidates candidates = new()
		{
			ModeTemperature = new Stamped<double>(asked.Value.Temperature, NewerOf(asked.Stamp, modeSince), asked.Origin),
			HandChange = driving.HandChange,

			// A warm-up and an unexpired hold are the integration's to run, so they are offered here only to keep
			// this pass from writing over one. A warm-up is the add-on's own, asked for a planned arrival.
			TimedHold = thermostat.HoldUnexpiredAt(now)
				? new Stamped<double>(holding, Stamp.Certain(now), ChangeOrigin.Thermostat)
				: null,
			WarmUp = thermostat.WarmUpRunning
				? new Stamped<double>(holding, Stamp.Certain(now), ChangeOrigin.SettingsPage)
				: null,
		};

		ResolvedTarget resolved = Precedence.Resolve(candidates);
		driving.LastSource = resolved.Source;

		// Held: a mode or a period does not move the room; a hand at the thermostat still does.
		if (gates.HeldBy is not null && resolved.Source is TargetSource.ModeTemperature)
			return 0;

		// A warm-up and an unexpired hold are the integration's, so what it is holding is what is in force.
		if (resolved.Source is TargetSource.WarmUp or TargetSource.TimedHold)
		{
			Offer(told, room.Id, resolved);

			return 0;
		}

		// A temperature the room is already holding replaces nothing, and nor does the one the last command landed as.
		if (Math.Abs(resolved.Temperature - holding) <= SameTemperature || LandedAsAsked(driving, resolved.Temperature, holding))
		{
			driving.LastCommanded = resolved.Temperature;
			Offer(told, room.Id, resolved);

			return 0;
		}

		ActionOutcome outcome = await _thermostats
			.SetTemperatureAsync(room.Thermostat!, resolved.Temperature, token)
			.ConfigureAwait(false);

		if (outcome != ActionOutcome.Called)
			return 0;

		driving.LastCommanded = resolved.Temperature;
		driving.CommandedAt = now;
		driving.LandedAs = null;
		driving.HandChange = null;

		Offer(told, room.Id, resolved);

		await _reports
			.ReportAsync(
				HeatingReport.About(WhatHappened.RoomHolds, now, room.Id) with
				{
					Temperature = resolved.Temperature,
					Named = resolved.Source.ToString()
				},
				token)
			.ConfigureAwait(false);

		return 1;
	}

	// Offered only where the number or where it came from moved: the record's stamp contest would refuse the same
	// temperature arriving again as a change, and every steady minute would then read as a refusal.
	private void Offer(Dictionary<string, Stamped<double>> told, string roomId, ResolvedTarget resolved)
	{
		if (_inForce.Temperatures.TryGetValue(roomId, out Stamped<double> held)
			&& held.Value == resolved.Temperature
			&& held.Origin == resolved.Origin)
			return;

		told[roomId] = new Stamped<double>(resolved.Temperature, resolved.Stamp, resolved.Origin) { By = resolved.By };
	}

	/// <summary>Starts the warm-up for a planned arrival, at the moment the planner says the heating has to begin.</summary>
	private async Task<int> WarmUpForTheArrivalAsync(
		RoomSlice room,
		ThermostatNow thermostat,
		RoomDriving driving,
		DateTimeOffset deadline,
		double wanted,
		DateTimeOffset now,
		double? outside,
		CancellationToken token)
	{
		if (thermostat.RoomTemperature is not { } roomTemperature || thermostat.WarmUpRunning)
			return 0;

		// Asked once per arrival. A different arrival carries a different deadline, so the comparison tells the
		// two apart without anything having to name one.
		if (driving.ArrivalAsked == deadline)
			return 0;

		double rate = Estimate(room, outside, now).DegreesPerHour;

		if (WarmUp.DueNow(roomTemperature, wanted, deadline, now, rate, _defaults) is not { } plan)
			return 0;

		ActionOutcome outcome = await _thermostats
			.WarmRoomByAsync(room.Thermostat!, wanted, deadline, token)
			.ConfigureAwait(false);

		if (outcome != ActionOutcome.Called)
			return 0;

		driving.ArrivalAsked = deadline;
		_record.Asked(room.Id, wanted, deadline, now);

		HeatingReport report = WarmUp.Report(room.Id, plan) is { } cannotReach
			? HeatingReport.About(WhatHappened.CannotWarmInTime, now, room.Id) with
			{
				Temperature = cannotReach.TemperatureWanted,
				Against = cannotReach.TemperatureItReaches,
				Deadline = deadline
			}
			: HeatingReport.About(WhatHappened.WarmUpStarted, now, room.Id) with
			{
				Temperature = wanted,
				Deadline = deadline
			};

		await _reports.ReportAsync(report, token).ConfigureAwait(false);

		return 1;
	}

	/// <summary>Watches the room, and writes back what it has learnt from it.</summary>
	private async Task<RoomSlice> LearnAsync(
		RoomSlice room,
		ThermostatNow thermostat,
		RoomDriving driving,
		DateTimeOffset now,
		double? outside,
		CancellationToken token)
	{
		// Nothing is learnt from a room publishing no target: both watchers measure against the temperature it
		// was asked to hold, and it was asked for none.
		if (thermostat.Target is not { } holding)
			return room;

		RoomObservation observation = new(
			now,
			thermostat.RoomTemperature,
			holding,
			thermostat.HeaterCommandedOn,
			thermostat.WarmUpRunning,
			outside,
			thermostat.PowerWatts,
			thermostat.HasAPowerMeter);

		RoomSlice after = room;
		RiseOutcome rise = driving.Rises.Observe(observation);

		if (rise.Measurement is { } measurement)
		{
			List<WarmingRateMeasurement> kept = [.. after.RateMeasurements, measurement];

			if (kept.Count > RisesKept)
				kept.RemoveRange(0, kept.Count - RisesKept);

			after = after with { RateMeasurements = kept };

			WarmingRateEstimate estimate = Estimate(after, outside, now);
			LearntNumber rate = after.Learnt.WarmingRate.Observe(estimate.DegreesPerHour, now, _defaults);

			after = after with { Learnt = after.Learnt with { WarmingRate = rate } };

			await _thermostats
				.SetWarmingRateAsync(room.Thermostat!, rate.Value, measurement.OutdoorTemperature, token)
				.ConfigureAwait(false);

			await _reports
				.ReportAsync(
					HeatingReport.About(WhatHappened.WarmingRateLearnt, now, room.Id) with
					{
						DegreesPerHour = rate.Value,
						Against = measurement.OutdoorTemperature
					},
					token)
				.ConfigureAwait(false);
		}
		else if (rise.Verdict is RiseVerdict.NoPowerDrawn or RiseVerdict.HeaterNeverRan)
		{
			await _reports
				.ReportAsync(HeatingReport.About(WhatHappened.RoseWithoutHeating, now, room.Id), token)
				.ConfigureAwait(false);
		}

		if (driving.Settling.Observe(observation, after.Learnt.OutdoorTerm.Value) is { } fit)
		{
			LearntNumber term = after.Learnt.OutdoorTerm.Observe(fit.PerDegree, now, _defaults);

			if (Math.Abs(term.Value - after.Learnt.OutdoorTerm.Value) > 1e-9)
			{
				await _thermostats
					.SetRegulationAsync(room.Thermostat!, outdoorShiftPerDegree: term.Value, token: token)
					.ConfigureAwait(false);

				await _reports
					.ReportAsync(
						HeatingReport.About(WhatHappened.OutdoorTermLearnt, now, room.Id) with
						{
							Temperature = fit.MeanShortfall,
							Against = fit.OutdoorTemperature
						},
						token)
					.ConfigureAwait(false);
			}

			after = after with { Learnt = after.Learnt with { OutdoorTerm = term } };
		}

		if (driving.Losses.Observe(observation) is { } loss)
		{
			List<HeatLossMeasurement> kept = [.. after.HeatLossMeasurements, loss];

			if (kept.Count > RisesKept)
				kept.RemoveRange(0, kept.Count - RisesKept);

			after = after with { HeatLossMeasurements = kept };
		}

		return after;
	}

	private WarmingRateEstimate Estimate(RoomSlice room, double? outside, DateTimeOffset now) =>
		WarmingRate.Estimate(
			room.RateMeasurements,
			room.Learnt.WarmingRate.StartingValue,
			outside ?? 0.0,
			now,
			_defaults);

	/// <summary>A target this pass did not ask for, set by somebody the room yields to; answers it where it is new.</summary>
	private Stamped<double>? NoteAHandChange(
		ThermostatNow thermostat,
		double holding,
		double asked,
		RoomDriving driving,
		bool automationsCountAsHand,
		DateTimeOffset now)
	{
		// While a warm-up or a hold runs, the published target is theirs and says nothing about a person.
		if (thermostat.WarmUpRunning || thermostat.HoldUnexpiredAt(now))
			return null;

		// What the mode asks moves nothing whoever set it, so the room is back on the plan; without this every start
		// reads a steady room's standing target as somebody's.
		if (Math.Abs(holding - asked) <= SameTemperature)
		{
			driving.HandChange = null;
			return null;
		}

		bool ours = (driving.LastCommanded is { } commanded && Math.Abs(holding - commanded) <= SameTemperature)
			|| (driving.LandedAs is { } landed && Math.Abs(holding - landed) <= SameTemperature);
		bool alreadyKnown = driving.HandChange is { } standing && Math.Abs(holding - standing.Value) <= SameTemperature;

		if (ours || alreadyKnown)
			return null;

		// A target set within the echo window after this add-on's own command is that command landing, rounded or
		// clamped by the thermostat, never a person.
		if (IsOwnEcho(thermostat, driving))
		{
			driving.LandedAs = holding;
			return null;
		}

		ChangesAtTheThermostats.Who who = _changes.WhoSet(thermostat.EntityId, holding, thermostat.Context);

		// Lighting's rule: a person or a device always counts, another automation where the room or the house says so.
		if (!new OverrideDetector(House, automationsCountAsHand).IsManual(who.Origin))
			return null;

		driving.HandChange = new Stamped<double>(
			holding,
			Stamp.Certain(thermostat.TargetSetAt ?? now),
			ChangeOrigin.Thermostat)
		{
			By = new SetBy(who.Context?.Id, who.Context?.ParentId, who.UserId, who.KeptWords, who.AutomationId)
		};

		_history.Changed(thermostat.RoomId, driving.HandChange.Value.Stamp.SetAt, who.KeptWords, who.UserId, who.AutomationId);

		return driving.HandChange;
	}

	private static bool LandedAsAsked(RoomDriving driving, double resolved, double holding) =>
		driving.LastCommanded is { } commanded
		&& Math.Abs(resolved - commanded) <= SameTemperature
		&& driving.LandedAs is { } landed
		&& Math.Abs(holding - landed) <= SameTemperature;

	private bool IsOwnEcho(ThermostatNow thermostat, RoomDriving driving) =>
		driving.CommandedAt is { } commandedAt
		&& thermostat.TargetSetAt is { } setAt
		&& setAt >= commandedAt
		&& setAt - commandedAt <= TimeSpan.FromSeconds(House.SelfEchoWindowSeconds);

	// The mode coming into force is itself a change, so its temperature is stamped at the later of the two: when
	// somebody typed the number, and when the mode it belongs to took over. Without that, a hand change made after
	// the number was typed would survive the house switching to away.
	private static Stamp NewerOf(Stamp typed, DateTimeOffset? modeSince) =>
		modeSince is { } since && since > typed.SetAt ? Stamp.Certain(since) : typed;

	private RoomDriving DrivingOf(string roomId)
	{
		if (!_driving.TryGetValue(roomId, out RoomDriving? driving))
		{
			driving = new RoomDriving(_defaults);
			_driving[roomId] = driving;
		}

		return driving;
	}

	/// <summary>What this process remembers about one room between passes. A restart costs one re-assertion.</summary>
	private sealed class RoomDriving
	{
		public RoomDriving(PlannerDefaults defaults)
		{
			Rises = new WarmingRateWatcher(defaults);
			Settling = new HoldingTermWatcher(defaults);
			Losses = new HeatLossWatcher(defaults);
		}

		public WarmingRateWatcher Rises { get; }

		public HoldingTermWatcher Settling { get; }

		public HeatLossWatcher Losses { get; }

		/// <summary>The setback the room was last given, or <c>null</c> where it holds none.</summary>
		public double? SetbackTemperature { get; set; }

		public string? SetbackWords { get; set; }

		/// <summary>The thermostat's minimum where it is tonight's floor, which the room page explains.</summary>
		public double? SetbackThermostatFloor { get; set; }

		public double? LastCommanded { get; set; }

		/// <summary>When this process last sent the room a temperature, which the echo window is measured from.</summary>
		public DateTimeOffset? CommandedAt { get; set; }

		/// <summary>What the thermostat made of that command inside the echo window, rounded or clamped.</summary>
		public double? LandedAs { get; set; }

		public bool? LastSwitched { get; set; }

		public Stamped<double>? HandChange { get; set; }

		/// <summary>The arrival this room's warm-up was last asked for, so the same one is not asked for twice.</summary>
		public DateTimeOffset? ArrivalAsked { get; set; }

		/// <summary>What chose the room's temperature on the last pass that weighed one.</summary>
		public TargetSource? LastSource { get; set; }

		/// <summary>The room's last event, which the next is compared against so a steady room stays quiet.</summary>
		public AreaSnapshot? Published { get; set; }
	}
}
