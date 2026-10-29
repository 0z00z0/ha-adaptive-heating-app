using NetDaemon.HassModel.Entities;

namespace Adaptive.Engine;

/// <summary>Edge-triggered on/off subscriptions, on <c>SubscribeSafe</c> so a thrown handler is logged, not fatal.</summary>
/// <remarks>
///     These filter on the new state only, so they fire on unavailable to on as well as off to on. Requiring the old
///     state to be off drops a sensor that comes back online already reporting motion.
/// </remarks>
public static class EntityObservableExtensions
{
	/// <summary>Runs <paramref name="handler"/> whenever the entity turns on.</summary>
	public static IDisposable WhenTurnsOn(this Entity entity, Action<StateChange> handler, ILogger logger) =>
		entity.StateChanges().Where(change => change.TurnedOn()).SubscribeSafe(handler, logger);

	/// <summary>Runs <paramref name="handler"/> whenever the entity turns off.</summary>
	public static IDisposable WhenTurnsOff(this Entity entity, Action<StateChange> handler, ILogger logger) =>
		entity.StateChanges().Where(change => change.TurnedOff()).SubscribeSafe(handler, logger);

	/// <summary>Runs <paramref name="handler"/> whenever the entity's state becomes <paramref name="state"/> (ordinal-ignore-case).</summary>
	public static IDisposable WhenStateBecomes(this Entity entity, string state, Action<StateChange> handler, ILogger logger) =>
		entity.StateChanges().Where(change => change.StateBecame(state)).SubscribeSafe(handler, logger);

	/// <summary>Runs <paramref name="handler"/> whenever the typed entity turns on.</summary>
	public static IDisposable WhenTurnsOn<TEntity, TAttributes>(
		this Entity<TEntity, EntityState<TAttributes>, TAttributes> entity,
		Action<StateChange<TEntity, EntityState<TAttributes>>> handler,
		ILogger logger)
		where TEntity : Entity<TEntity, EntityState<TAttributes>, TAttributes>
		where TAttributes : class =>
		entity.StateChanges().Where(change => change.New?.IsOn() ?? false).SubscribeSafe(handler, logger);

	/// <summary>Runs <paramref name="handler"/> whenever the typed entity turns off.</summary>
	public static IDisposable WhenTurnsOff<TEntity, TAttributes>(
		this Entity<TEntity, EntityState<TAttributes>, TAttributes> entity,
		Action<StateChange<TEntity, EntityState<TAttributes>>> handler,
		ILogger logger)
		where TEntity : Entity<TEntity, EntityState<TAttributes>, TAttributes>
		where TAttributes : class =>
		entity.StateChanges().Where(change => change.New?.IsOff() ?? false).SubscribeSafe(handler, logger);
}
