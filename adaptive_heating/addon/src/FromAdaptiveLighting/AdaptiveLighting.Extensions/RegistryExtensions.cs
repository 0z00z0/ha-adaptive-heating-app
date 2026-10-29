using NetDaemon.HassModel;
using NetDaemon.HassModel.Entities;

namespace Adaptive.Engine;

/// <summary>The <see cref="IHaRegistry"/> questions the engine asks, each in one expression.</summary>
public static class RegistryExtensions
{
	/// <summary>Every area id the registry knows.</summary>
	public static IReadOnlyList<string> AreaIds(this IHaRegistry registry) =>
		[.. registry.Areas.Select(area => area.Id).OfType<string>()];

	/// <summary>Whether an area with id <paramref name="areaId"/> exists.</summary>
	public static bool AreaExists(this IHaRegistry registry, string areaId) => registry.GetArea(areaId) is not null;

	/// <summary>The display name for <paramref name="areaId"/>, or <c>null</c> when the area is unknown or unnamed.</summary>
	/// <remarks>An area id is a slug, so this is the only thing that turns <c>kjeller_bad</c> back into a readable name.</remarks>
	public static string? AreaNameOf(this IHaRegistry registry, string areaId) =>
		registry.GetArea(areaId)?.Name is { Length: > 0 } name ? name : null;

	/// <summary>The entity ids assigned to <paramref name="areaId"/>, distinct and ordinal, empty when it is unknown.</summary>
	public static IReadOnlyList<string> EntityIdsInArea(this IHaRegistry registry, string areaId) =>
		registry.GetArea(areaId) is { } area
			? [.. area.Entities.Select(entity => entity.EntityId).Distinct(StringComparer.Ordinal)]
			: [];

	/// <summary>The floor <paramref name="areaId"/> sits on.</summary>
	/// <remarks>Floors are optional in Home Assistant, so <c>null</c> is an ordinary answer.</remarks>
	public static Floor? FloorOf(this IHaRegistry registry, string areaId) => registry.GetArea(areaId)?.Floor;

	/// <summary>Every label the house has, id and name, whether or not anything carries it.</summary>
	public static IReadOnlyList<RegistryLabel> KnownLabels(this IHaRegistry registry) => Pairs(registry.Labels);

	/// <summary>The labels on <paramref name="entityId"/>, each in both forms.</summary>
	public static IReadOnlyList<RegistryLabel> LabelsOf(this IHaRegistry registry, string entityId) =>
		Pairs(registry.GetEntityRegistration(entityId)?.Labels);

	/// <summary>Whether <paramref name="entityId"/> carries <paramref name="label"/>, by id when the house knows that id.</summary>
	public static bool HasLabel(this IHaRegistry registry, string entityId, string label) =>
		LabelMatch.Carries(registry.LabelsOf(entityId), registry.KnownLabels(), label);

	/// <summary>The labels on area <paramref name="areaId"/> itself, each in both forms.</summary>
	public static IReadOnlyList<RegistryLabel> LabelsOfArea(this IHaRegistry registry, string areaId) =>
		Pairs(registry.GetArea(areaId)?.Labels);

	/// <summary>Whether area <paramref name="areaId"/> carries <paramref name="label"/>, by id when the house knows that id.</summary>
	public static bool AreaHasLabel(this IHaRegistry registry, string areaId, string label) =>
		LabelMatch.Carries(registry.LabelsOfArea(areaId), registry.KnownLabels(), label);

	// A label with no id cannot be matched or translated, so it is dropped. An unnamed one reads as its own id.
	private static IReadOnlyList<RegistryLabel> Pairs(IEnumerable<Label>? labels) =>
		labels is null
			? []
			: [.. labels
				.Where(label => label.Id is { Length: > 0 })
				.Select(label => new RegistryLabel(label.Id!, label.Name is { Length: > 0 } name ? name : label.Id!))];

	/// <summary>The id of the device <paramref name="entityId"/> belongs to, or <c>null</c> when it belongs to none.</summary>
	/// <remarks>
	///     What duplicate-hardware checks are decided on. A group helper and a template entity have no device, so null
	///     is ordinary and a group is never mistaken for a duplicate of the entities inside it.
	/// </remarks>
	public static string? DeviceOf(this IHaRegistry registry, string entityId) =>
		registry.GetEntityRegistration(entityId)?.Device?.Id is { Length: > 0 } device ? device : null;

	/// <summary>The entity ids on device <paramref name="deviceId"/>, empty when it is unknown.</summary>
	public static IReadOnlyList<string> EntityIdsOnDevice(this IHaRegistry registry, string deviceId) =>
		registry.GetDevice(deviceId) is { } device
			? [.. device.Entities.Select(entity => entity.EntityId).Distinct(StringComparer.Ordinal)]
			: [];

	/// <summary>The entity ids in <paramref name="areaId"/> that are in <paramref name="domain"/>.</summary>
	public static IReadOnlyList<string> EntityIdsInAreaByDomain(this IHaRegistry registry, string areaId, string domain) =>
		[.. registry.EntityIdsInArea(areaId).Where(id => id.HasDomain(domain))];
}
