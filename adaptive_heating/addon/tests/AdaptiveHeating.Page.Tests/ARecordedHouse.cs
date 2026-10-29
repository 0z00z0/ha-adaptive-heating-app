using System.Text.Json;
using System.Text.Json.Serialization;

using Adaptive.Engine;
using Adaptive.Settings;

using Microsoft.Extensions.Logging.Abstractions;

using NetDaemon.HassModel;

namespace AdaptiveHeating.Page.Tests;

/// <summary>One entity as the registries and the state machine describe it.</summary>
// The field names are the integration's own, so one recorded house feeds both halves' rules.
internal sealed record RecordedEntity(
	[property: JsonPropertyName("entity_id")] string EntityId,
	[property: JsonPropertyName("area_id")] string AreaId,
	[property: JsonPropertyName("labels")] IReadOnlyList<string> Labels,
	[property: JsonPropertyName("platform")] string Platform)
{
	[JsonPropertyName("hidden")]
	public bool Hidden { get; init; }

	[JsonPropertyName("available")]
	public bool Available { get; init; } = true;

	[JsonPropertyName("device_id")]
	public string? DeviceId { get; init; }

	[JsonPropertyName("device_class")]
	public string? DeviceClass { get; init; }

	/// <summary>A group helper's own entities, which its state lists under <c>entity_id</c>.</summary>
	[JsonPropertyName("members")]
	public IReadOnlyList<string> Members { get; init; } = [];
}

/// <summary>One Area, with the labels on the Area itself and the floor it sits on.</summary>
internal sealed record RecordedArea(
	[property: JsonPropertyName("area_id")] string AreaId,
	[property: JsonPropertyName("labels")] IReadOnlyList<string> Labels)
{
	[JsonIgnore]
	public string? Name { get; init; }

	[JsonIgnore]
	public AreaFloor? Floor { get; init; }
}

/// <summary>A house as the registries report it, which is what both halves' room rules are measured against.</summary>
internal sealed class ARecordedHouse : IAreaRegistry, IEntityFacts
{
	public const string Heating = GlobalConfig.DefaultIncludeLabel;

	public const string Excluded = GlobalConfig.DefaultExcludeLabel;

	/// <summary>This integration's own platform, whose climate entities are rooms and never a room's heaters.</summary>
	public const string Ours = "adaptive_heating";

	/// <summary>Somebody else's integration, which is what a real heater is published by.</summary>
	public const string Theirs = "shelly";

	private readonly List<RecordedArea> _areas;
	private readonly List<RecordedEntity> _entities;
	private readonly AHouseUnderTest _states;

	public ARecordedHouse(IEnumerable<RecordedArea> areas, IEnumerable<RecordedEntity> entities)
	{
		_areas = [.. areas];
		_entities = [.. entities];
		_states = new AHouseUnderTest([.. _entities.Select(StateOf)]);
	}

	/// <summary>The house as the entity model holds it, which is what the resolver reads a state through.</summary>
	public IHaContext Context => _states;

	public IReadOnlyList<string> AreaIds => [.. _areas.Select(area => area.AreaId)];

	public IReadOnlyList<RegistryLabel> KnownLabels =>
	[
		.. _areas.SelectMany(area => area.Labels)
			.Concat(_entities.SelectMany(entity => entity.Labels))
			.Distinct(StringComparer.Ordinal)
			.Select(label => new RegistryLabel(label, label))
	];

	/// <summary>The resolver over this house, with <paramref name="global"/> or the document's own defaults.</summary>
	public AreaEntityResolver Resolver(GlobalConfig? global = null) =>
		new(_states, this, global ?? new GlobalConfig(), this, Ours, NullLogger.Instance);

	public bool AreaExists(string areaId) => _areas.Any(area => string.Equals(area.AreaId, areaId, StringComparison.Ordinal));

	public string? NameOf(string areaId) =>
		_areas.FirstOrDefault(area => string.Equals(area.AreaId, areaId, StringComparison.Ordinal))?.Name;

	public IReadOnlyList<string> EntitiesInArea(string areaId) =>
		[.. _entities.Where(entity => string.Equals(entity.AreaId, areaId, StringComparison.Ordinal)).Select(entity => entity.EntityId)];

	public IReadOnlyList<RegistryLabel> LabelsOf(string entityId) =>
		[.. Entity(entityId)?.Labels.Select(label => new RegistryLabel(label, label)) ?? []];

	public IReadOnlyList<RegistryLabel> LabelsOfArea(string areaId) =>
		[.. _areas.FirstOrDefault(area => string.Equals(area.AreaId, areaId, StringComparison.Ordinal))?.Labels
			.Select(label => new RegistryLabel(label, label)) ?? []];

	public string? DeviceOf(string entityId) => Entity(entityId)?.DeviceId;

	public IReadOnlyList<string> EntitiesOnDevice(string deviceId) =>
		[.. _entities.Where(entity => string.Equals(entity.DeviceId, deviceId, StringComparison.Ordinal)).Select(entity => entity.EntityId)];

	public AreaFloor? FloorOf(string areaId) =>
		_areas.FirstOrDefault(area => string.Equals(area.AreaId, areaId, StringComparison.Ordinal))?.Floor;

	public bool IsHidden(string entityId) => Entity(entityId)?.Hidden ?? false;

	public string? PlatformOf(string entityId) => Entity(entityId)?.Platform;

	/// <summary>This house as JSON, in the shape the integration's own rule reads.</summary>
	public string AsJson() =>
		JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
		{
			["areas"] = _areas,
			["entities"] = _entities,
			["own_platform"] = Ours,
			["include_label"] = Heating,
			["exclude_label"] = Excluded
		});

	/// <summary>A heater somebody else's integration published, carrying the discovery label.</summary>
	public static RecordedEntity Heater(string entityId, string areaId) =>
		new(entityId, areaId, [Heating], Theirs);

	/// <summary>A temperature sensor carrying the discovery label.</summary>
	public static RecordedEntity Sensor(string entityId, string areaId) =>
		new(entityId, areaId, [Heating], Theirs) { DeviceClass = "temperature" };

	/// <summary>This integration's own thermostat for a room, which is the room and never its heater.</summary>
	public static RecordedEntity OurThermostat(string entityId, string areaId) =>
		new(entityId, areaId, [Heating], Ours);

	public static RecordedArea Area(string areaId, params string[] labels) => new(areaId, labels);

	private RecordedEntity? Entity(string entityId) =>
		_entities.FirstOrDefault(entity => string.Equals(entity.EntityId, entityId, StringComparison.Ordinal));

	private static EntityState StateOf(RecordedEntity entity) =>
		CoreUnderTest.State(
			entity.EntityId,
			entity.Available ? "on" : "unavailable",
			entity.DeviceClass is { Length: > 0 } deviceClass
				? $$"""{ "device_class": "{{deviceClass}}" }"""
				: entity.Members.Count > 0
					? $$"""{ "entity_id": [{{string.Join(", ", entity.Members.Select(member => $"\"{member}\""))}}] }"""
					: "{ }");
}
