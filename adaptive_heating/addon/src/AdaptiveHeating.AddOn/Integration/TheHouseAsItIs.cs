using NetDaemon.HassModel;

namespace AdaptiveHeating.AddOn.Integration;

/// <summary>The house as NetDaemon's entity model holds it, and the one place the add-on reads a state from.</summary>
internal sealed class TheHouseAsItIs
{
	private readonly Func<IReadOnlyCollection<string>> _alsoNamed;

	private volatile Up? _up;

	public TheHouseAsItIs(Func<IReadOnlyCollection<string>> alsoNamed) =>
		_alsoNamed = alsoNamed ?? throw new ArgumentNullException(nameof(alsoNamed));

	/// <summary>Whether the model's caches are filled, so a read answers the house rather than nothing.</summary>
	public bool IsUp => _up is not null;

	/// <summary>Every entity the heating reads, as the model's cache holds it this moment.</summary>
	public IReadOnlyList<EntityState> Reads()
	{
		if (_up is not { } up)
			return [];

		IReadOnlyCollection<string> named = _alsoNamed();

		return [.. up.House.GetAllEntities()
			.Where(entity => WhatTheHeatingReads.Reads(entity.EntityId, named))
			.Select(entity => entity.EntityState)
			.OfType<EntityState>()];
	}

	/// <summary>One entity's state, or <c>null</c> where the house has none.</summary>
	public EntityState? StateOf(string entityId) => _up?.House.GetState(entityId);

	/// <summary>The model's own reading of the house, or <c>null</c> while the caches are not filled.</summary>
	public IHaContext? Context => _up?.House;

	/// <summary>The registries behind the model, or <c>null</c> while the caches are not filled.</summary>
	public IHaRegistry? Registry => _up?.Registry;

	/// <summary>The area registry as the engine asks it, or <c>null</c> while the caches are not filled.</summary>
	public IAreaRegistry? Areas => _up?.Areas;

	/// <summary>What the registry says about one entity: its Area, its labels, its device and whether it is hidden.</summary>
	public RegistryAnswer About(string entityId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(entityId);

		if (_up is not { } up)
			return RegistryAnswer.NothingKnownAbout(entityId);

		return new RegistryAnswer(
			entityId,
			up.House.GetAreaFromEntityId(entityId)?.Id,
			up.Registry.LabelsOf(entityId),
			up.Registry.DeviceOf(entityId),
			up.Hidden.Hidden(entityId),
			up.House.GetEntityRegistration(entityId)?.Platform);
	}

	/// <summary>Every entity the house holds in one domain, which is what the settings page offers from.</summary>
	public IReadOnlyList<string> EntityIdsInDomain(string domain) =>
		_up is { } up ? up.House.EntityIdsInDomain(domain) : [];

	/// <summary>The model's caches are filled and the registries are readable.</summary>
	public void CameUp(IHaContext house, IHaRegistry registry, WhatIsHidden hidden) =>
		_up = new Up(house, registry, hidden, new HaAreaRegistry(registry));

	/// <summary>The connection the caches were filled over has gone, so nothing may be read until the next one.</summary>
	public void HasGone() => _up = null;

	private sealed record Up(IHaContext House, IHaRegistry Registry, WhatIsHidden Hidden, HaAreaRegistry Areas);
}
