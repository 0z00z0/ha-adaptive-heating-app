using System.Diagnostics.CodeAnalysis;

using Adaptive.Settings;

using AdaptiveLighting.Engine;

using NetDaemon.HassModel.Entities;

namespace Adaptive.Engine;

/// <summary>The two entity-registry facts <see cref="IAreaRegistry"/> does not carry.</summary>
public interface IEntityFacts
{
	/// <summary>Whether a person has hidden the entity. A hidden entity is never a discovery candidate.</summary>
	bool IsHidden(string entityId);

	/// <summary>The integration that published the entity, or <c>null</c> where the registry does not say.</summary>
	string? PlatformOf(string entityId);
}

/// <summary>A room with every entity reference turned into a concrete id, ready to drive.</summary>
public sealed record ResolvedRoom(
	string Name,
	AreaSettings Settings,
	IReadOnlyList<string> Heaters,
	IReadOnlyList<string> TemperatureSensors)
{
	/// <summary>This room's own climate entity, which every action for the room targets.</summary>
	public string? Thermostat { get; init; }

	public string? WindowSensor { get; init; }

	public string? PowerSensor { get; init; }

	public PowerSensorSource PowerSensorFoundBy { get; init; }

	// Filled only under MoreThanOne.
	public IReadOnlyList<string> PowerSensorCandidates { get; init; } = [];

	/// <summary>The room this one borrows its reading from, by area id.</summary>
	public string? Neighbour { get; init; }

	public double? AwayTemperature { get; init; }
}

/// <summary>Why a room's power sensor is the one it is.</summary>
public enum PowerSensorSource
{
	None = 0,
	SameDevice,
	Label,
	Chosen,
	MoreThanOne
}

/// <summary>What discovery finds in one Area, before that room's hand-picked lists have had their say.</summary>
public sealed record AreaDiscovery(IReadOnlyList<string> Heaters, IReadOnlyList<string> TemperatureSensors);

/// <summary>Turns an <see cref="AreaConfig"/> into a <see cref="ResolvedRoom"/>, discovering what it left out.</summary>
// Discovery-first: an area id and a device class stay true across a rename, where a hand-listed entity does not.
public sealed class AreaEntityResolver
{
	private const string ClimateDomain = "climate";
	private const string SensorDomain = "sensor";
	private const string DeviceClassAttribute = "device_class";
	private const string TemperatureDeviceClass = "temperature";
	private const string PowerDeviceClass = "power";
	private const string UnavailableState = "unavailable";
	private const string UnknownState = "unknown";
	private const string GroupMembersAttribute = "entity_id";
	private const string BinarySensorDomain = "binary_sensor";
	private const string BatteryDeviceClass = "battery";

	/// <summary>The platform this integration publishes under: its thermostats and its reason sensors.</summary>
	public const string OurPlatform = "adaptive_heating";

	// A thermostat is driven by its setpoint and a switch by its relay; nothing else can be held. The same three
	// the integration's own rule takes, in the same order.
	private static readonly string[] HeaterDomains = ["switch", "input_boolean", ClimateDomain];

	private readonly IHaContext _ha;
	private readonly IAreaRegistry _registry;
	private readonly GlobalConfig _global;
	private readonly IEntityFacts _facts;
	private readonly string _ownPlatform;
	private readonly ILogger _logger;

	public AreaEntityResolver(
		IHaContext ha,
		IAreaRegistry registry,
		GlobalConfig global,
		IEntityFacts facts,
		string ownPlatform,
		ILogger logger)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(ownPlatform);

		_ha = ha ?? throw new ArgumentNullException(nameof(ha));
		_registry = registry ?? throw new ArgumentNullException(nameof(registry));
		_global = global ?? throw new ArgumentNullException(nameof(global));
		_facts = facts ?? throw new ArgumentNullException(nameof(facts));
		_ownPlatform = ownPlatform;
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <summary>What <paramref name="areaId"/> yields on its own, by the same rules <see cref="TryResolve"/> uses.</summary>
	public AreaDiscovery DiscoverArea(string areaId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(areaId);

		if (IsExcludedArea(areaId))
			return new AreaDiscovery([], []);

		return new AreaDiscovery(DiscoverHeaters(areaId), DiscoverTemperatureSensors(areaId));
	}

	/// <summary>Why <paramref name="areaId"/> is not offered as a room, in the order discovery applies its gates.</summary>
	// Read for the "set up rooms again" dialog, which otherwise drops a non-qualifying Area with no word said.
	public string ExplainWhyNotARoom(string areaId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(areaId);

		if (!_registry.AreaExists(areaId))
			return "no longer exists in Home Assistant.";

		if (IsExcludedArea(areaId))
			return $"carries the label '{LabelText(_global.ExcludeLabel)}' in Home Assistant, so the engine treats it as not there.";

		List<string> inArea = [.. _registry.EntitiesInArea(areaId).Where(IsHeaterDomain)];

		if (inArea.Count == 0)
			return "has no heater in Home Assistant, and a room with no heater has nothing to command.";

		List<string> notOurs = [.. inArea.Where(id => !IsOurs(id))];

		if (notOurs.Count == 0)
			return "holds only this integration's own thermostats, which are rooms and never a room's heaters.";

		List<string> visible = [.. notOurs.Where(id => !_facts.IsHidden(id))];

		if (visible.Count == 0)
			return $"has {Plural(notOurs.Count, "heater")} here, all hidden in Home Assistant.";

		List<string> notExcluded = [.. visible.Where(id => !IsExcluded(id))];

		if (notExcluded.Count == 0)
			return $"has {Plural(visible.Count, "heater")} here, all carrying the label '{LabelText(_global.ExcludeLabel)}'.";

		if (_global.IncludeLabel is { Length: > 0 } include)
		{
			List<string> included = [.. notExcluded.Where(IsIncluded)];

			if (included.Count == 0)
				return $"has {Plural(notExcluded.Count, "heater")} here, but none carries the label '{LabelText(include)}' that Home Assistant is set to require.";

			notExcluded = included;
		}

		if (notExcluded.Where(IsLive).ToList() is { Count: 0 })
			return notExcluded.Count == 1
				? "has one heater here, but it is unavailable in Home Assistant right now."
				: $"has {notExcluded.Count} heaters here, but they are all unavailable in Home Assistant right now.";

		// The per-device pass only ever narrows a non-empty candidate list, never empties it, so this is unreached
		// in practice. Kept as the honest answer if that ever stops holding.
		return "has heaters that discovery still did not turn into a room.";
	}

	private static string Plural(int count, string noun) => $"{count} {(count == 1 ? noun : noun + "s")}";

	/// <summary>Resolves <paramref name="area"/>, or explains why it cannot be.</summary>
	/// <returns><c>false</c> when the room must be skipped. A skipped room is never a reason to fail the house.</returns>
	public bool TryResolve(
		AreaConfig area,
		AreaSettings defaults,
		[NotNullWhen(true)] out ResolvedRoom? resolved,
		[NotNullWhen(false)] out string? error)
	{
		ArgumentNullException.ThrowIfNull(area);
		ArgumentNullException.ThrowIfNull(defaults);

		resolved = null;
		error = null;

		// The one place a name enters the engine. Everything downstream reads the resolved room's Name.
		string name = AreaNaming.DisplayName(area, _registry);
		string? areaId = null;

		if (area.AreaId is { Length: > 0 } configuredArea)
		{
			if (!_registry.AreaExists(configuredArea))
			{
				// A registry that has not answered yet lists nothing, and "Known ids: ." reads as a fault here
				// and not as a connection still coming up.
				error = _registry.AreaIds.Count > 0
					? $"Home Assistant has no area '{configuredArea}'. It must be the area's id, the slug, not its display name. Known ids: {string.Join(", ", _registry.AreaIds.Order(StringComparer.Ordinal))}."
					: $"Home Assistant has no area '{configuredArea}', and lists no areas at all yet; it may still be starting up.";

				return false;
			}

			if (IsExcludedArea(configuredArea))
			{
				error = $"Area '{configuredArea}' carries the label '{LabelText(_global.ExcludeLabel)}' in Home Assistant, so the engine treats it as not there. Remove the label to run it.";

				return false;
			}

			areaId = configuredArea;
		}

		// A hand-picked list bypasses both labels, discovery and this room's exclusions. The rules get no veto.
		List<string> heaters = area.Heaters is { Count: > 0 } named
			? [.. named]
			: WithoutExcluded(DiscoverHeaters(areaId), area);

		if (heaters.Count == 0)
		{
			error = NoHeatersError(areaId);

			return false;
		}

		List<string> sensors = area.TemperatureSensors is { Count: > 0 } namedSensors
			? [.. namedSensors]
			: WithoutExcluded(DiscoverTemperatureSensors(areaId), area);

		_logger.LogInformation(
			"Room {Room}: {HeaterCount} heaters ({Heaters}), {SensorCount} temperature sensors ({Sensors}).",
			name, heaters.Count, string.Join(", ", heaters.Select(Describe)), sensors.Count,
			sensors.Count > 0 ? string.Join(", ", sensors.Select(Describe)) : "(none)");

		(string? power, PowerSensorSource powerSource, IReadOnlyList<string> powerCandidates) =
			ResolvePowerSensor(area, areaId, heaters);

		resolved = new ResolvedRoom(name, area.Effective(defaults), heaters, sensors)
		{
			Thermostat = areaId is null ? null : ThermostatIn(areaId),
			WindowSensor = Trimmed(area.WindowSensor),
			PowerSensor = power,
			PowerSensorFoundBy = powerSource,
			PowerSensorCandidates = powerCandidates,
			Neighbour = Trimmed(area.Neighbour),
			AwayTemperature = area.AwayTemperature
		};

		return true;
	}

	/// <summary>This room's own reason sensor, which says why the room is not heating.</summary>
	public string? ReasonSensorIn(string areaId) =>
		_registry.EntitiesInArea(areaId)
			.Where(id => id.HasDomain(SensorDomain))
			.Where(IsOurs)
			.Order(StringComparer.Ordinal)
			.FirstOrDefault();

	/// <summary>The battery entities on each sensor's own device, read from the registry as it stands now.</summary>
	// A sensor with no device, a group or a template, has no battery and is left out.
	public List<MotionBattery> BatteriesOf(IReadOnlyList<string> sensors)
	{
		ArgumentNullException.ThrowIfNull(sensors);

		List<MotionBattery> batteries = [];

		foreach (string sensor in sensors)
		{
			if (_registry.DeviceOf(sensor) is not { Length: > 0 } device)
				continue;

			IReadOnlyList<string> onDevice = [.. _registry.EntitiesOnDevice(device).Order(StringComparer.Ordinal)];
			string? low = onDevice.FirstOrDefault(id => id.HasDomain(BinarySensorDomain) && IsBattery(id));
			string? level = onDevice.FirstOrDefault(id => id.HasDomain(SensorDomain) && IsBattery(id));

			if (low is not null || level is not null)
				batteries.Add(new MotionBattery(sensor, low, level));
		}

		return batteries;
	}

	private bool IsBattery(string entityId) =>
		string.Equals(DeviceClassOf(entityId), BatteryDeviceClass, StringComparison.OrdinalIgnoreCase);

	/// <summary>This room's own climate entity: the one in its Area that this integration published.</summary>
	public string? ThermostatIn(string areaId) =>
		_registry.EntitiesInArea(areaId)
			.Where(id => id.HasDomain(ClimateDomain))
			.Where(IsOurs)
			.Order(StringComparer.Ordinal)
			.FirstOrDefault();

	// A blank entity id would reach a service call, which throws on one, from inside a room's pass.
	private static string? Trimmed(string? entityId) =>
		string.IsNullOrWhiteSpace(entityId) ? null : entityId.Trim();

	// Re-runs discovery without the include label, because an Area with no heater at all must not be told to go
	// and label one.
	private string NoHeatersError(string? areaId)
	{
		if (areaId is null)
			return "No heaters: the room has neither an AreaId to discover from nor a Heaters list.";

		if (_global.IncludeLabel is { Length: > 0 } include && DiscoverHeaters(areaId, respectIncludeLabel: false).Count > 0)
			return $"No heater in '{areaId}' carries the label '{include}'. Clear the label filter, or label the heaters in Home Assistant.";

		return $"No heater discovered in area '{areaId}'. Assign heaters to the area in Home Assistant, or list them by hand.";
	}

	private List<string> DiscoverHeaters(string? areaId) => DiscoverHeaters(areaId, respectIncludeLabel: true);

	// respectIncludeLabel is false only on the failure path, to tell "no heaters" from "all filtered out".
	private List<string> DiscoverHeaters(string? areaId, bool respectIncludeLabel)
	{
		if (areaId is null)
			return [];

		List<string> candidates =
		[
			.. _registry.EntitiesInArea(areaId)
				.Where(IsHeaterDomain)
				.Where(id => !IsOurs(id))
				.Where(id => !_facts.IsHidden(id))
				.Where(id => !IsExcluded(id))
				.Where(id => !respectIncludeLabel || IsIncluded(id))
				.Where(IsLive)
				.Distinct(StringComparer.Ordinal)
		];

		return OneEntityPerDevice(areaId, OpenGroups(areaId, candidates), "heater");
	}

	// Counts leaves, never direct members, so a group of groups reports the entities it actually holds.
	private string Describe(string entityId)
	{
		HashSet<string> seen = new(StringComparer.Ordinal);
		int leaves = 0;

		Count(entityId);

		return seen.Count > 1 ? $"{entityId} (a group of {leaves})" : entityId;

		void Count(string id)
		{
			if (!seen.Add(id))
				return;

			IReadOnlyList<string> members = _ha.GetState(id).AttrStringList(GroupMembersAttribute);

			if (members.Count == 0)
				leaves++;

			foreach (string member in members)
				Count(member);
		}
	}

	/// <summary>A group of heaters stands for its members, each driven as a relay of its own.</summary>
	// The relay guard and power check work per relay. Members skip the include label; same rule as core/rooms.py.
	private List<string> OpenGroups(string areaId, List<string> candidates)
	{
		List<string> taken = [];
		HashSet<string> seen = new(StringComparer.Ordinal);

		foreach (string candidate in candidates)
			Open(candidate);

		return taken;

		void Open(string entityId)
		{
			if (!seen.Add(entityId))
				return;

			IReadOnlyList<string> members = _ha.GetState(entityId).AttrStringList(GroupMembersAttribute);

			if (members.Count == 0)
			{
				taken.Add(entityId);
				return;
			}

			foreach (string member in members)
			{
				if (IsHeaterDomain(member) && IsLive(member) && !IsOurs(member) && !IsExcluded(member)
					&& !InAnotherArea(member, areaId))
					Open(member);
			}
		}
	}

	private bool InAnotherArea(string entityId, string areaId) =>
		_registry.AreaIds.Any(other =>
			!string.Equals(other, areaId, StringComparison.Ordinal)
			&& _registry.EntitiesInArea(other).Contains(entityId, StringComparer.Ordinal));

	// No availability gate, deliberately: the integration's own rule has none either, and how stale a reading may
	// be is the room's four sensor settings, which is a finer test than "unavailable".
	private List<string> DiscoverTemperatureSensors(string? areaId)
	{
		if (areaId is null)
			return [];

		List<string> candidates =
		[
			.. _registry.EntitiesInArea(areaId)
				.Where(IsTemperatureSensor)
				.Where(id => !_facts.IsHidden(id))
				.Where(id => !IsExcluded(id))
				.Where(IsIncluded)
				.Distinct(StringComparer.Ordinal)
		];

		return OneEntityPerDevice(areaId, candidates, "temperature sensor");
	}

	// A pick is the slot's whole set. Then the heaters' own devices, then the area under the include label; two in
	// one step is no answer. No availability gate: a silent meter is the integration's verdict.
	private (string? Meter, PowerSensorSource Source, IReadOnlyList<string> Candidates) ResolvePowerSensor(
		AreaConfig area,
		string? areaId,
		IReadOnlyList<string> heaters)
	{
		if (Trimmed(area.PowerSensor) is { } chosen)
			return (chosen, PowerSensorSource.Chosen, []);

		HashSet<string> left = new(area.ExcludeEntities ?? [], StringComparer.Ordinal);

		List<string> onHeaters =
		[
			.. heaters
				.Select(_registry.DeviceOf)
				.OfType<string>()
				.Where(device => device.Length > 0)
				.SelectMany(_registry.EntitiesOnDevice)
				.Where(id => IsPowerMeterCandidate(id, left))
				.Distinct(StringComparer.Ordinal)
				.Order(StringComparer.Ordinal)
		];

		if (onHeaters.Count > 0)
			return OneOf(onHeaters, PowerSensorSource.SameDevice);

		if (areaId is null)
			return (null, PowerSensorSource.None, []);

		List<string> labelled =
		[
			.. _registry.EntitiesInArea(areaId)
				.Where(id => IsPowerMeterCandidate(id, left))
				.Where(IsIncluded)
				.Distinct(StringComparer.Ordinal)
				.Order(StringComparer.Ordinal)
		];

		return labelled.Count > 0 ? OneOf(labelled, PowerSensorSource.Label) : (null, PowerSensorSource.None, []);

		static (string?, PowerSensorSource, IReadOnlyList<string>) OneOf(List<string> found, PowerSensorSource source) =>
			found.Count == 1 ? (found[0], source, []) : (null, PowerSensorSource.MoreThanOne, found);
	}

	private bool IsPowerMeterCandidate(string entityId, HashSet<string> leftOut) =>
		entityId.HasDomain(SensorDomain)
		&& string.Equals(DeviceClassOf(entityId), PowerDeviceClass, StringComparison.OrdinalIgnoreCase)
		&& !IsOurs(entityId)
		&& !_facts.IsHidden(entityId)
		&& !IsExcluded(entityId)
		&& !leftOut.Contains(entityId);

	/// <summary>Keeps one entity per piece of hardware: several entities on one Home Assistant device are one thing.</summary>
	// Ordinal by entity id, which is the integration's own tie-break, so both halves keep the same entity. An
	// entity with no device of its own stands alone and is never a duplicate.
	private List<string> OneEntityPerDevice(string areaId, List<string> candidates, string noun)
	{
		HashSet<string> claimed = new(StringComparer.Ordinal);
		List<string> kept = [];
		List<string> folded = [];

		foreach (string candidate in candidates.Order(StringComparer.Ordinal))
		{
			if (_registry.DeviceOf(candidate) is { Length: > 0 } device && !claimed.Add(device))
			{
				folded.Add(candidate);
				continue;
			}

			kept.Add(candidate);
		}

		if (folded.Count > 0)
			_logger.LogWarning(
				"Area '{Area}': {Count} further {Kind} entities ({Folded}) sit on a Home Assistant device another one "
				+ "already stands for, so they are the same hardware and only the first of each is used.",
				areaId, folded.Count, noun, string.Join(", ", folded));

		return kept;
	}

	private static bool IsHeaterDomain(string entityId) =>
		HeaterDomains.Any(domain => entityId.HasDomain(domain));

	private bool IsTemperatureSensor(string entityId) =>
		entityId.HasDomain(SensorDomain)
		&& string.Equals(DeviceClassOf(entityId), TemperatureDeviceClass, StringComparison.OrdinalIgnoreCase);

	/// <summary>Whether this integration published the entity, which makes it a room and never a room's heater.</summary>
	private bool IsOurs(string entityId) =>
		string.Equals(_facts.PlatformOf(entityId), _ownPlatform, StringComparison.OrdinalIgnoreCase);

	/// <summary>Whether the entity is something Home Assistant can actually act on right now.</summary>
	// The registry lists rows: a disabled or never-loaded entity still comes back from EntitiesInArea with no
	// state at all, and unavailable and unknown go too.
	private bool IsLive(string entityId)
	{
		EntityState? state = _ha.GetState(entityId);

		if (state is null)
		{
			_logger.LogDebug("Ignoring {EntityId}: a registry entry with no state is not a device.", entityId);

			return false;
		}

		if (string.Equals(state.State, UnavailableState, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(state.State, UnknownState, StringComparison.OrdinalIgnoreCase))
		{
			_logger.LogDebug("Ignoring {EntityId}: {State}.", entityId, state.State);

			return false;
		}

		return true;
	}

	// The per-room twin of the exclude label, by id. Discovered lists only: a hand-picked list is the owner
	// overruling discovery, and no rule re-filters it.
	private static List<string> WithoutExcluded(List<string> discovered, AreaConfig area)
	{
		if (area.ExcludeEntities is not { Count: > 0 } excluded)
			return discovered;

		HashSet<string> drop = new(excluded, StringComparer.Ordinal);

		return [.. discovered.Where(id => !drop.Contains(id))];
	}

	private string? DeviceClassOf(string entityId) => _ha.AttrString(entityId, DeviceClassAttribute);

	private bool IsExcluded(string entityId) => HasLabel(entityId, _global.ExcludeLabel);

	/// <summary>Whether the Area's own registration carries the exclude label, which reads as the Area not existing.</summary>
	public bool IsExcludedArea(string? areaId) =>
		areaId is { Length: > 0 }
		&& Carries(_registry.LabelsOfArea(areaId), _global.ExcludeLabel);

	// No label configured means everything passes. Checked after the exclusion, never instead of it, so an entity
	// carrying both labels stays out.
	private bool IsIncluded(string entityId) =>
		_global.IncludeLabel is not { Length: > 0 } include || HasLabel(entityId, include);

	private bool HasLabel(string entityId, string label) => Carries(_registry.LabelsOf(entityId), label);

	/// <summary>Whether <paramref name="carried"/> holds the label id <paramref name="labelId"/>. A label name never matches.</summary>
	public static bool Carries(IReadOnlyList<RegistryLabel>? carried, string? labelId) =>
		carried is not null
		&& labelId is { Length: > 0 }
		&& carried.Any(label => string.Equals(label.Id, labelId, StringComparison.Ordinal));

	// The document stores an id once it has been translated, and an id is not what the house sees in HA.
	private string LabelText(string label) => LabelMatch.DisplayName(_registry.KnownLabels, label);
}
