namespace AdaptiveHeating.AddOn.Integration;

/// <summary>The house's Areas and the two entity facts beside them, for as long as the process runs.</summary>
// Read live, so no snapshot can go stale. While the connection is down there is nothing to answer with, and
// InvalidOperationException is what the engine's own callers already expect there.
internal sealed class TheAreasOfTheHouse : IAreaRegistry, IEntityFacts
{
	private const string NotConnected = "Home Assistant's area registry has not been read yet.";

	private readonly TheHouseAsItIs _house;

	public TheAreasOfTheHouse(TheHouseAsItIs house) =>
		_house = house ?? throw new ArgumentNullException(nameof(house));

	private IAreaRegistry Live => _house.Areas ?? throw new InvalidOperationException(NotConnected);

	public IReadOnlyList<string> AreaIds => Live.AreaIds;

	public IReadOnlyList<RegistryLabel> KnownLabels => Live.KnownLabels;

	public bool AreaExists(string areaId) => Live.AreaExists(areaId);

	public string? NameOf(string areaId) => Live.NameOf(areaId);

	public IReadOnlyList<string> EntitiesInArea(string areaId) => Live.EntitiesInArea(areaId);

	public IReadOnlyList<RegistryLabel> LabelsOf(string entityId) => Live.LabelsOf(entityId);

	public IReadOnlyList<RegistryLabel> LabelsOfArea(string areaId) => Live.LabelsOfArea(areaId);

	public string? DeviceOf(string entityId) => Live.DeviceOf(entityId);

	public IReadOnlyList<string> EntitiesOnDevice(string deviceId) => Live.EntitiesOnDevice(deviceId);

	public AreaFloor? FloorOf(string areaId) => Live.FloorOf(areaId);

	// Answers rather than throwing: nothing known reads as not hidden, and discovery only runs while connected.
	public bool IsHidden(string entityId) => _house.About(entityId).Hidden;

	public string? PlatformOf(string entityId) => _house.About(entityId).Platform;
}
