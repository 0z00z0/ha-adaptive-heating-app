namespace Adaptive.Engine;

/// <summary>The real <see cref="IAreaRegistry"/>, reading <see cref="IHaRegistry"/> live so no snapshot can go stale.</summary>
public sealed class HaAreaRegistry : IAreaRegistry
{
	private readonly IHaRegistry _registry;

	public HaAreaRegistry(IHaRegistry registry) => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

	/// <inheritdoc/>
	public IReadOnlyList<string> AreaIds => _registry.AreaIds();

	/// <inheritdoc/>
	public bool AreaExists(string areaId) => _registry.AreaExists(areaId);

	/// <inheritdoc/>
	public string? NameOf(string areaId) => _registry.AreaNameOf(areaId);

	/// <inheritdoc/>
	public IReadOnlyList<string> EntitiesInArea(string areaId) => _registry.EntityIdsInArea(areaId);

	/// <inheritdoc/>
	public IReadOnlyList<RegistryLabel> KnownLabels => _registry.KnownLabels();

	/// <inheritdoc/>
	public IReadOnlyList<RegistryLabel> LabelsOf(string entityId) => _registry.LabelsOf(entityId);

	/// <inheritdoc/>
	public IReadOnlyList<RegistryLabel> LabelsOfArea(string areaId) => _registry.LabelsOfArea(areaId);

	/// <inheritdoc/>
	public string? DeviceOf(string entityId) => _registry.DeviceOf(entityId);

	/// <inheritdoc/>
	public IReadOnlyList<string> EntitiesOnDevice(string deviceId) => _registry.EntityIdsOnDevice(deviceId);

	/// <inheritdoc/>
	/// <remarks>
	///     A floor with no id reads as no floor. The group key is the id, so an anonymous floor would collect every
	///     floorless area into one group.
	/// </remarks>
	public AreaFloor? FloorOf(string areaId) =>
		_registry.FloorOf(areaId) is { Id: { Length: > 0 } id } floor
			? new AreaFloor(id, floor.Name is { Length: > 0 } name ? name : id, floor.Level)
			: null;
}
