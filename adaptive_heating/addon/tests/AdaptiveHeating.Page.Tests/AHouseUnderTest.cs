using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;

using NetDaemon.HassModel;

namespace AdaptiveHeating.Page.Tests;

/// <summary>One recorded call through the entity model, kept so a test can assert on what went out.</summary>
internal sealed record ModelCall(string Domain, string Service, ServiceTarget? Target, object? Data);

/// <summary>Caches a test has already filled by hand, so nothing reaches for Home Assistant to fill them.</summary>
internal sealed class ACacheAlreadyFilled : ICacheManager
{
	public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>The house as the entity model holds it: a state per entity and a stream of changes, written by hand.</summary>
// Every registry lookup answers nothing: HassModel's Area and EntityRegistration have no public constructor.
internal sealed class AHouseUnderTest : IHaContext, IHaRegistry
{
	private readonly Subject<StateChange> _changes = new();
	private readonly Subject<Event> _events = new();
	private readonly Dictionary<string, EntityState> _states = new(StringComparer.Ordinal);

	public AHouseUnderTest(params EntityState[] states) => Holds(states);

	public List<ModelCall> Calls { get; } = [];

	IObservable<Event> IHaContext.Events => _events;

	public IReadOnlyCollection<EntityRegistration> Entities => [];

	public IReadOnlyCollection<Device> Devices => [];

	public IReadOnlyCollection<Area> Areas => [];

	public IReadOnlyCollection<Floor> Floors => [];

	public IReadOnlyCollection<Label> Labels => [];

	/// <summary>Puts states in without announcing them, which is what a fresh connection's read leaves behind.</summary>
	public void Holds(params IEnumerable<EntityState> states)
	{
		foreach (EntityState state in states)
			_states[state.EntityId] = state;
	}

	/// <summary>Home Assistant reporting that one entity has changed, which is what the model's stream carries.</summary>
	public void Reports(EntityState state)
	{
		ArgumentNullException.ThrowIfNull(state);

		_states.TryGetValue(state.EntityId, out EntityState? old);
		_states[state.EntityId] = state;

		_changes.OnNext(new StateChange(new Entity(this, state.EntityId), old, state));
	}

	/// <summary>Drops an entity, which is what Home Assistant reports nothing for.</summary>
	public void HasGone(string entityId) => _states.Remove(entityId);

	public EntityState? GetState(string entityId) => _states.GetValueOrDefault(entityId);

	public IReadOnlyList<Entity> GetAllEntities() => [.. _states.Keys.Select(id => new Entity(this, id))];

	public IObservable<StateChange> StateAllChanges() => _changes;

	public void CallService(string domain, string service, ServiceTarget? target = null, object? data = null) =>
		Calls.Add(new ModelCall(domain, service, target, data));

	public Task<JsonElement?> CallServiceWithResponseAsync(
		string domain,
		string service,
		ServiceTarget? target = null,
		object? data = null)
	{
		Calls.Add(new ModelCall(domain, service, target, data));

		return Task.FromResult<JsonElement?>(null);
	}

	public Area? GetAreaFromEntityId(string entityId) => null;

	public Entity Entity(string entityId) => new(this, entityId);

	public EntityRegistration? GetEntityRegistration(string entityId) => null;

	public Device? GetDevice(string deviceId) => null;

	public Area? GetArea(string areaId) => null;

	public Floor? GetFloor(string floorId) => null;

	public Label? GetLabel(string labelId) => null;

	public void SendEvent(string eventType, object? data) => SendEvent(eventType, data, null);

	public void SendEvent(string eventType, object? data, Context? context) =>
		_events.OnNext(new Event { EventType = eventType, DataElement = JsonSerializer.SerializeToElement(data), Context = context });
}
