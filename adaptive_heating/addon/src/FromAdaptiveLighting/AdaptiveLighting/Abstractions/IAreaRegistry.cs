namespace Adaptive.Engine;

/// <summary>A floor as the engine and UI need it: identity, display name, and stacking order.</summary>
/// <remarks><c>Level</c> is HA's own number and orders floors; null when the house never set one.</remarks>
public sealed record AreaFloor(string Id, string Name, int? Level);

/// <summary>Everything the engine needs from the Home Assistant area registry, and nothing else.</summary>
/// <remarks>
///     A seam because <c>NetDaemon.HassModel.Entities.Area</c> cannot be constructed outside its own assembly, so
///     a test can implement <c>IHaContext</c> but never return an area with entities in it.
/// </remarks>
public interface IAreaRegistry
{
	IReadOnlyList<string> AreaIds { get; }

	bool AreaExists(string areaId);

	/// <remarks>Read, never stored. <see cref="AreaNaming"/> is the only place the fallback order lives.</remarks>
	string? NameOf(string areaId);

	/// <summary>The entity ids assigned to <paramref name="areaId"/>, directly or through a device.</summary>
	IReadOnlyList<string> EntitiesInArea(string areaId);

	/// <summary>Every label the house has, whether or not anything carries it.</summary>
	/// <remarks>What decides whether a stored value is an id or a name. Empty while the registry is unreadable.</remarks>
	IReadOnlyList<RegistryLabel> KnownLabels { get; }

	/// <summary>The registry labels on <paramref name="entityId"/>.</summary>
	IReadOnlyList<RegistryLabel> LabelsOf(string entityId);

	/// <summary>
	///     The registry labels on the area itself. Distinct from <see cref="LabelsOf"/>: labelling an area does not
	///     label the entities in it.
	/// </summary>
	IReadOnlyList<RegistryLabel> LabelsOfArea(string areaId);

	/// <summary>
	///     The device <paramref name="entityId"/> belongs to, or <c>null</c> for a group helper, a template entity
	///     and an unknown id alike.
	/// </summary>
	/// <remarks>Two entities are the same hardware when they share a device id; a group helper has none.</remarks>
	string? DeviceOf(string entityId);

	/// <summary>The entity ids on device <paramref name="deviceId"/>, empty when it is unknown.</summary>
	IReadOnlyList<string> EntitiesOnDevice(string deviceId);

	AreaFloor? FloorOf(string areaId);
}
