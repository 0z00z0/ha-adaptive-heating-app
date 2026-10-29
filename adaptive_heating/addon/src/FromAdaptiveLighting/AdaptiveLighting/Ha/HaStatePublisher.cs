using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Engine;

namespace AdaptiveLighting.Ha;

/// <summary>Publishes room snapshots as Home Assistant events, and logs them.</summary>
public sealed class HaStatePublisher : IStatePublisher
{
	public const string EventType = "adaptive_heating_area";

	// Asked on every publish: the add-on's model of the house comes and goes with the connection.
	private readonly Func<IHaContext?> _ha;
	private readonly ILogger _logger;

	public HaStatePublisher(Func<IHaContext?> ha, ILogger logger)
	{
		_ha = ha ?? throw new ArgumentNullException(nameof(ha));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <inheritdoc/>
	public void Publish(AreaSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		_logger.LogInformation(
			"Area {Area} is {State} ({Reason}); house {Mode}, period {Period}, target {Target}, temperature {Temperature}.",
			snapshot.AreaName, snapshot.State, snapshot.Reason, HouseModeName.Of(snapshot.Mode),
			snapshot.PeriodName, snapshot.Target, snapshot.Temperature);

		// A throw here would take the pass with it.
		try
		{
			if (_ha() is not { } ha)
				return;

			// Fields here are only ever added, never renamed or removed: consumers bind them by name.
			ha.SendEvent(EventType, new
			{
				area = snapshot.AreaName,
				area_id = snapshot.AreaId,
				room_id = snapshot.RoomId,
				state = snapshot.State.ToString(),
				reason = snapshot.Reason.ToString(),
				// "Home" for the everyday kind. Consumers match on this word, so only HouseModeName writes it.
				mode = HouseModeName.Of(snapshot.Mode),
				house_mode_value = snapshot.HouseModeValue,
				kill_switch_active = snapshot.KillSwitchActive,
				period = snapshot.PeriodName,
				period_id = snapshot.PeriodId,
				target = snapshot.Target,
				temperature = snapshot.Temperature,
				timestamp = snapshot.Timestamp,
				last_command_at = snapshot.LastCommandAt,
				next_change_at = snapshot.NextChangeAt,
				next_change_from = snapshot.NextChangeFrom,
				auto_on_blocked_by = snapshot.AutoOnBlockedBy?.ToString(),
				auto_on_blocking_entity = snapshot.AutoOnBlockingEntity,
				is_held = snapshot.IsHeld,
				held_by = snapshot.HeldBy,
				target_from = snapshot.TargetFrom,
				heaters_not_responding = snapshot.HeatersNotResponding,
				heater_count = snapshot.HeaterCount,
				temperature_sensors_low_battery = snapshot.LowBatteries?.Select(battery => new
				{
					entity_id = battery.SensorId,
					level_pct = battery.LevelPct
				}).ToArray(),
				changed_by = snapshot.ChangedBy,
				changed_by_user_id = snapshot.ChangedByUserId,
				changed_at = snapshot.ChangedAt,
				levels_from_room = snapshot.LevelsFromRoom,
				// Flat fields, so an automation trigger can read one without walking an object.
				is_anyone_home = snapshot.IsAnyoneHome,
				mode_forced_kind = snapshot.Forced?.Kind.ToString(),
				mode_forced_option = snapshot.Forced?.OptionValue,
				mode_forced_source = snapshot.Forced?.Source.ToString(),
				mode_forced_by = snapshot.Forced?.EntityId,
				mode_forced_by_state = snapshot.Forced?.EntityState,
				active_mode_name = snapshot.ActiveModeName
			});
		}
		catch (Exception exception)
		{
			_logger.LogWarning(exception, "Could not publish the snapshot for area {Area}.", snapshot.AreaName);
		}
	}
}
