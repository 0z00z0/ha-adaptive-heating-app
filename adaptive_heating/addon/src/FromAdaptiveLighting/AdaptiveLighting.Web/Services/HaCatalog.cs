using Adaptive.Engine;
using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging.Abstractions;

using NetDaemon.HassModel.Entities;

namespace Adaptive.UI;

/// <param name="FriendlyName">Home Assistant's <c>friendly_name</c>, or the id when it has none.</param>
public sealed record EntityOption(string EntityId, string FriendlyName, string? AreaName)
{
	public string Label => AreaName is { Length: > 0 } area
		? $"{FriendlyName} — {area} ({EntityId})"
		: $"{FriendlyName} ({EntityId})";
}

public sealed record LabelOption(string Id, string Name);

/// <summary>
///     One area, as a picker offers it. The counts are the resolver's, after discovery and the ghost filter, never
///     the registry's row count, which says nothing about heating and includes disabled rows.
/// </summary>
/// <param name="Id">The registry area id: the slug the config stores.</param>
/// <param name="HeaterCount">Heaters discovery yields here, groups opened and the unavailable dropped.</param>
public sealed record AreaOption(string Id, string Name, int HeaterCount, int SensorCount)
{
	public string Label => $"{NameAndId} — {Counts}";

	public string Counts => $"{Pluralise(HeaterCount, "heater")}, {Pluralise(SensorCount, "temperature sensor")}";

	/// <summary>An area with no heaters is the common, silent mistake.</summary>
	public bool HasHeaters => HeaterCount > 0;

	private string NameAndId => string.Equals(Name, Id, StringComparison.Ordinal) ? Id : $"{Name} ({Id})";

	private static string Pluralise(int count, string noun) => $"{count} {noun}{(count == 1 ? string.Empty : "s")}";
}

/// <summary>What discovery finds in one area, offered first in the room page's pickers.</summary>
public sealed record AreaEntities(IReadOnlyList<EntityOption> Heaters, IReadOnlyList<EntityOption> TemperatureSensors)
{
	public static AreaEntities Empty { get; } = new([], []);
}

/// <param name="Error">Why discovery fails, in the resolver's own words.</param>
public sealed record AreaPreview(ResolvedRoom? Resolved, string? Error);

/// <summary>Turns the Home Assistant registry into things a person can pick from.</summary>
/// <remarks>Scoped: one per Blazor circuit, with the registry read live, never snapshotted.</remarks>
public sealed class HaCatalog
{
	private const string FriendlyNameAttribute = "friendly_name";
	private const string DeviceClassAttribute = "device_class";
	private const string OptionsAttribute = "options";
	private const string SunEntityId = "sun.sun";
	private const string NextRisingAttribute = "next_rising";
	private const string NextSettingAttribute = "next_setting";

	// Asked on every read: the add-on's model of the house comes and goes with the connection, and null reads as
	// Home Assistant not answering.
	private readonly Func<IHaContext?> _ha;
	private readonly Func<IHaRegistry?> _registry;
	private readonly IAreaRegistry? _areas;
	private readonly IEntityFacts? _facts;
	private readonly ILogger<HaCatalog> _logger;

	/// <param name="areas">The area registry the room page's discovery reads; without it every room reads as unresolved.</param>
	public HaCatalog(
		Func<IHaContext?> ha,
		Func<IHaRegistry?> registry,
		ILoggerFactory loggerFactory,
		IAreaRegistry? areas = null,
		IEntityFacts? facts = null)
	{
		_ha = ha ?? throw new ArgumentNullException(nameof(ha));
		_registry = registry ?? throw new ArgumentNullException(nameof(registry));
		ArgumentNullException.ThrowIfNull(loggerFactory);
		_logger = loggerFactory.CreateLogger<HaCatalog>();
		_areas = areas;
		_facts = facts;
	}

	/// <summary>The area registry as the engine sees it, for what a page needs beyond pickers: floors, chiefly.</summary>
	public IAreaRegistry? AreaRegistry => _areas;

	/// <summary>Whether Home Assistant answered the last question asked of it.</summary>
	/// <remarks>
	///     Kestrel serves the moment the process is up; NetDaemon connects afterwards and its state cache throws
	///     until it has. Every page here is reachable in that window, so <c>false</c> is a state to render, not a
	///     fault: the pickers empty and the editor falls back to free text.
	/// </remarks>
	public bool IsHomeAssistantReady { get; private set; } = true;

	/// <summary>Whether Home Assistant is answering, as opposed to merely not throwing.</summary>
	/// <remarks>
	///     The registry answers an empty list where it has nothing, so something has to come back: an area or a
	///     switch. Both are read, so a throw in either counts. Reads the registry and every state: call it per
	///     load, never per render.
	/// </remarks>
	public bool IsHomeAssistantResponding(GlobalConfig global)
	{
		ArgumentNullException.ThrowIfNull(global);

		int areas = Areas(global).Count;
		int switches = EntitiesInDomains("input_boolean", "switch").Count;

		return IsHomeAssistantReady && areas + switches > 0;
	}

	/// <summary>An entity's friendly name, or its id where Home Assistant gives none.</summary>
	public string FriendlyNameOrId(string entityId) => FriendlyNameOf(entityId) ?? entityId;

	/// <summary>Every area the registry knows, each labelled with what a room there would resolve to.</summary>
	/// <returns>The areas, ordered by display name. Empty when Home Assistant has not answered.</returns>
	public IReadOnlyList<AreaOption> Areas(GlobalConfig global)
	{
		ArgumentNullException.ThrowIfNull(global);

		if (_areas is not { } areas)
			return [];

		try
		{
			return [.. areas.AreaIds
				.Where(areaId => areaId is { Length: > 0 })
				.Select(areaId => Option(areaId, AreaNaming.OfArea(areas, areaId) ?? areaId, global))
				.OrderBy(area => area.Name, StringComparer.CurrentCulture)];
		}
		catch (InvalidOperationException exception)
		{
			IsHomeAssistantReady = false;
			_logger.LogDebug(exception, "The area registry is not available yet.");
			return [];
		}
	}

	/// <summary>Works out what setting <paramref name="scope"/> up again would do, against the house as it is.</summary>
	/// <remarks>
	///     A pass-through to <see cref="AreaSetupService.Plan"/>; the rules stay in the engine. The resolver is
	///     built from the document being edited, so a plan honours what is on screen, not what was last saved.
	/// </remarks>
	/// <param name="config">The document being edited. Not mutated.</param>
	public SetupPlan PlanSetup(HeatingConfig config, IReadOnlyCollection<string> scope)
	{
		ArgumentNullException.ThrowIfNull(config);
		ArgumentNullException.ThrowIfNull(scope);

		try
		{
			return AreaSetupService.Plan(config, _areas ?? throw new InvalidOperationException(NotUp), Resolver(config.Global), scope);
		}
		catch (InvalidOperationException exception)
		{
			IsHomeAssistantReady = false;
			_logger.LogDebug(exception, "The registry is not available, so no setup plan can be made.");
			return new SetupPlan([], [], []);
		}
	}

	/// <summary>Why an area is no room, in the engine's own words, or <c>null</c> while Home Assistant cannot say.</summary>
	public string? WhyNotARoom(string? areaId, GlobalConfig global)
	{
		ArgumentNullException.ThrowIfNull(global);

		try
		{
			return areaId is { Length: > 0 } ? Resolver(global).ExplainWhyNotARoom(areaId) : null;
		}
		catch (InvalidOperationException)
		{
			return null;
		}
	}

	private AreaOption Option(string areaId, string name, GlobalConfig global)
	{
		AreaDiscovery discovered = Discover(areaId, global);
		return new AreaOption(areaId, name, discovered.Heaters.Count, discovered.TemperatureSensors.Count);
	}

	/// <summary>Every registry label with both its id and its name, for the label pickers.</summary>
	/// <remarks>
	///     Empty is a real answer, not a failure: most houses have never made a label. A picker built on this has
	///     to render that case as instructions, not an empty dropdown.
	/// </remarks>
	public IReadOnlyList<LabelOption> LabelOptions()
	{
		try
		{
			return [.. (_registry() ?? throw new InvalidOperationException(NotUp)).Labels
				.Where(label => label.Id is { Length: > 0 })
				.Select(label => new LabelOption(label.Id!, label.Name is { Length: > 0 } name ? name : label.Id!))
				.OrderBy(option => option.Name, StringComparer.CurrentCulture)];
		}
		catch (InvalidOperationException)
		{
			IsHomeAssistantReady = false;
			return [];
		}
	}

	/// <param name="domains">Domain prefixes without the dot: <c>light</c>, <c>input_boolean</c>.</param>
	public IReadOnlyList<EntityOption> EntitiesInDomains(params string[] domains)
	{
		ArgumentNullException.ThrowIfNull(domains);

		return Enumerate(entityId => domains.Contains(entityId.Domain() ?? "", StringComparer.Ordinal));
	}

	/// <summary>
	///     Every entity that can name a date or time, for the "reset at a time" picker: <c>input_datetime.*</c>,
	///     <c>time.*</c> and <c>datetime.*</c> helpers, plus any <c>sensor.*</c> whose <c>device_class</c> is
	///     <c>timestamp</c> or <c>date</c>. Ordered by friendly name; empty when Home Assistant has not answered.
	/// </summary>
	public IReadOnlyList<EntityOption> DateTimeEntities()
	{
		IReadOnlyList<EntityOption> helpers = EntitiesInDomains("input_datetime", "time", "datetime");
		IReadOnlyList<EntityOption> timeSensors = EntitiesWithDeviceClass("sensor", ["timestamp", "date"]);

		return [.. helpers
			.Concat(timeSensors)
			.OrderBy(option => option.FriendlyName, StringComparer.CurrentCulture)];
	}

	/// <summary>
	///     Every entity in <paramref name="domain"/> whose <c>device_class</c> is one of
	///     <paramref name="deviceClasses"/>.
	/// </summary>
	/// <param name="deviceClasses">The device classes that qualify. Empty offers the whole domain.</param>
	public IReadOnlyList<EntityOption> EntitiesWithDeviceClass(string domain, IReadOnlyList<string> deviceClasses)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(domain);
		ArgumentNullException.ThrowIfNull(deviceClasses);

		return Enumerate(entityId =>
			string.Equals(entityId.Domain(), domain, StringComparison.Ordinal)
			&& (deviceClasses.Count == 0
				|| deviceClasses.Contains(DeviceClassOf(entityId) ?? string.Empty, StringComparer.OrdinalIgnoreCase)));
	}

	public string? FriendlyNameOf(string? entityId)
	{
		if (string.IsNullOrWhiteSpace(entityId))
			return null;

		return ReadAttribute(TryGetState(entityId), FriendlyNameAttribute);
	}

	/// <summary>
	///     The live <c>options</c> of an <c>input_select</c>, or empty when HA cannot answer or the entity has none.
	/// </summary>
	public IReadOnlyList<string> SelectOptionsOf(string? entityId) =>
		string.IsNullOrWhiteSpace(entityId) ? [] : ReadStringArray(TryGetState(entityId), OptionsAttribute);

	/// <summary>
	///     The live state of <paramref name="entityId"/> as a usable value (trimmed; <c>unknown</c>/<c>unavailable</c>
	///     folded to <c>null</c>), or <c>null</c> when blank or Home Assistant cannot answer. Used to mark the option a
	///     house-mode select currently stands on.
	/// </summary>
	public string? CurrentStateOf(string? entityId) =>
		string.IsNullOrWhiteSpace(entityId) ? null : TryGetState(entityId).AsUsableState();

	/// <summary>Today's sunrise/sunset from the house's sun entity in the household's wall time, or <c>(null, null)</c>.</summary>
	/// <param name="sunEntity">The document's <c>Global.SunEntity</c>; blank reads <c>sun.sun</c>.</param>
	public (TimeOnly? Sunrise, TimeOnly? Sunset) SunTimesToday(TimeZoneInfo zone, string? sunEntity = null)
	{
		EntityState? state = TryGetState(sunEntity is { Length: > 0 } named ? named : SunEntityId);
		return (ReadSunTime(state, NextRisingAttribute), ReadSunTime(state, NextSettingAttribute));

		TimeOnly? ReadSunTime(EntityState? state, string attribute) =>
			state.AttrDateTimeOffset(attribute) is { } parsed
				? TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(parsed, zone).DateTime)
				: null;
	}

	/// <summary>An entity's whole state, for the room page's head and its evidence table.</summary>
	public EntityState? StateOf(string? entityId) => string.IsNullOrWhiteSpace(entityId) ? null : TryGetState(entityId);

	/// <summary>Runs the engine's own area resolver against <paramref name="area"/> as it stands in the editor.</summary>
	/// <param name="area">The area being edited. Not mutated.</param>
	public AreaPreview PreviewArea(AreaConfig area, AreaSettings defaults, GlobalConfig global)
	{
		ArgumentNullException.ThrowIfNull(area);
		ArgumentNullException.ThrowIfNull(defaults);
		ArgumentNullException.ThrowIfNull(global);

		try
		{
			return Resolver(global).TryResolve(area, defaults, out ResolvedRoom? resolved, out string? error)
				? new AreaPreview(resolved, null)
				: new AreaPreview(null, error);
		}
		catch (InvalidOperationException)
		{
			IsHomeAssistantReady = false;
			return new AreaPreview(null, "Home Assistant is not connected yet, so discovery cannot be previewed.");
		}
	}

	/// <summary>What discovery finds in <paramref name="areaId"/>, named for the pickers.</summary>
	public AreaEntities EntitiesInArea(string? areaId, GlobalConfig global)
	{
		ArgumentNullException.ThrowIfNull(global);

		if (string.IsNullOrWhiteSpace(areaId))
			return AreaEntities.Empty;

		AreaDiscovery discovered = Discover(areaId, global);

		return new AreaEntities(Describe(discovered.Heaters), Describe(discovered.TemperatureSensors));
	}

	/// <summary>What the area holds besides what discovery settled on, for the fold that overrules discovery.</summary>
	/// <remarks>Derived by subtraction, never by re-deciding: the label rules stay in <see cref="AreaEntityResolver"/>.</remarks>
	/// <param name="areaId">The area to look in.</param>
	/// <param name="global">The house settings the labels come from.</param>
	/// <param name="labelledOnly">Whether to offer only what carries <see cref="GlobalConfig.IncludeLabel"/>.</param>
	public AreaEntities OtherEntitiesInArea(string? areaId, GlobalConfig global, bool labelledOnly)
	{
		ArgumentNullException.ThrowIfNull(global);

		if (string.IsNullOrWhiteSpace(areaId) || _areas is not { } areas)
			return AreaEntities.Empty;

		AreaDiscovery discovered = Discover(areaId, global);
		IReadOnlyList<string> inArea;

		try
		{
			inArea = areas.EntitiesInArea(areaId);
		}
		catch (InvalidOperationException)
		{
			return AreaEntities.Empty;
		}

		bool Offered(string entityId) =>
			!string.Equals(_facts?.PlatformOf(entityId), AreaEntityResolver.OurPlatform, StringComparison.OrdinalIgnoreCase)
			&& (!labelledOnly
				|| global.IncludeLabel is not { Length: > 0 } include
				|| AreaEntityResolver.Carries(areas.LabelsOf(entityId), include));

		IReadOnlyList<EntityOption> Rest(IReadOnlyList<string> claimed, Func<string, bool> qualifies)
		{
			HashSet<string> taken = new(claimed, StringComparer.Ordinal);

			return Describe([.. inArea.Where(qualifies).Where(id => !taken.Contains(id)).Where(Offered).Distinct(StringComparer.Ordinal)]);
		}

		return new AreaEntities(
			Rest(discovered.Heaters, id => HeaterDomains.Contains(id.Domain() ?? "", StringComparer.Ordinal)),
			Rest(discovered.TemperatureSensors, id =>
				string.Equals(id.Domain(), "sensor", StringComparison.Ordinal)
				&& string.Equals(DeviceClassOf(id), "temperature", StringComparison.OrdinalIgnoreCase)));
	}

	/// <summary>This room's reason sensor, or <c>null</c> where the integration has published none.</summary>
	public string? ReasonSensorIn(string? areaId, GlobalConfig global)
	{
		ArgumentNullException.ThrowIfNull(global);

		try
		{
			return areaId is { Length: > 0 } && _areas is not null ? Resolver(global).ReasonSensorIn(areaId) : null;
		}
		catch (InvalidOperationException)
		{
			return null;
		}
	}

	/// <summary>The low batteries among <paramref name="sensors"/>, read live; empty while every one is fine.</summary>
	public IReadOnlyList<SensorBattery> LowBatteriesOf(IReadOnlyList<string> sensors, GlobalConfig global)
	{
		ArgumentNullException.ThrowIfNull(sensors);
		ArgumentNullException.ThrowIfNull(global);

		try
		{
			return [.. Resolver(global).BatteriesOf(sensors)
				.Select(battery => battery.LowFrom(CurrentStateOf(battery.LowEntity), CurrentStateOf(battery.LevelEntity)))
				.OfType<SensorBattery>()];
		}
		catch (InvalidOperationException)
		{
			return [];
		}
	}

	/// <summary>Every heater Home Assistant holds: what the heaters picker falls back to.</summary>
	public IReadOnlyList<EntityOption> Heaters() => EntitiesInDomains(HeaterDomains);

	/// <summary>Every temperature sensor Home Assistant holds: what the sensors picker falls back to.</summary>
	public IReadOnlyList<EntityOption> TemperatureSensors() => EntitiesWithDeviceClass("sensor", ["temperature"]);

	private static readonly string[] HeaterDomains = ["switch", "input_boolean", "climate"];

	private AreaDiscovery Discover(string areaId, GlobalConfig global)
	{
		try
		{
			return Resolver(global).DiscoverArea(areaId);
		}
		catch (InvalidOperationException)
		{
			IsHomeAssistantReady = false;
			return new AreaDiscovery([], []);
		}
	}

	private List<EntityOption> Describe(IReadOnlyList<string> entityIds) =>
		[.. entityIds
			.Select(id => new EntityOption(id, FriendlyNameOrId(id), AreaNameOf(id)))
			.OrderBy(option => option.FriendlyName, StringComparer.CurrentCulture)];

	// Its own logger would name every room's heaters once a second from an open page.
	private AreaEntityResolver Resolver(GlobalConfig global) =>
		new(
			Ha,
			_areas ?? throw new InvalidOperationException(NotUp),
			global,
			_facts ?? throw new InvalidOperationException(NotUp),
			AreaEntityResolver.OurPlatform,
			NullLogger.Instance);

	/// <summary>Whether Home Assistant knows <paramref name="entityId"/>. Drives the "unknown entity" warning.</summary>
	public bool Knows(string? entityId) =>
		!string.IsNullOrWhiteSpace(entityId) && TryGetState(entityId) is not null;

	private IReadOnlyList<EntityOption> Enumerate(Func<string, bool> predicate)
	{
		try
		{
			return [.. Ha.GetAllEntities()
				.Select(entity => entity.EntityId)
				.Where(predicate)
				.Select(entityId => new EntityOption(
					entityId,
					ReadAttribute(TryGetState(entityId), FriendlyNameAttribute) ?? entityId,
					AreaNameOf(entityId)))
				.OrderBy(option => option.FriendlyName, StringComparer.CurrentCulture)];
		}
		catch (InvalidOperationException exception)
		{
			IsHomeAssistantReady = false;
			_logger.LogDebug(exception, "The entity registry is not available yet.");
			return [];
		}
	}

	private string? AreaNameOf(string entityId)
	{
		try
		{
			return (_registry() ?? throw new InvalidOperationException(NotUp)).GetEntityRegistration(entityId)?.Area?.Name;
		}
		catch (InvalidOperationException)
		{
			return null;
		}
	}

	private string? DeviceClassOf(string entityId) => ReadAttribute(TryGetState(entityId), DeviceClassAttribute);

	private static string? ReadAttribute(EntityState? state, string attribute) => state.AttrString(attribute);

	/// <summary>Reads an attribute holding a list of strings, or an empty list when absent or the wrong shape.</summary>
	private static IReadOnlyList<string> ReadStringArray(EntityState? state, string attribute) => state.AttrStringList(attribute);

	private EntityState? TryGetState(string entityId)
	{
		try
		{
			return Ha.GetState(entityId);
		}
		catch (InvalidOperationException)
		{
			IsHomeAssistantReady = false;
			return null;
		}
	}

	private const string NotUp = "Home Assistant has not answered yet.";

	private IHaContext Ha => _ha() ?? throw new InvalidOperationException(NotUp);
}
