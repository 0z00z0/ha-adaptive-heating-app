using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using NetDaemon.HassModel.Entities;

namespace Adaptive.Engine;

public sealed record ModeToggle(
	string Label,
	string Description,
	string EntityId,
	bool IsOn,
	bool IsAvailable,
	bool CanToggle,
	string Meaning);

/// <summary>One live option of the house-mode select, with the single kind and reset summary the config gave it.</summary>
public sealed record HouseModeOptionView(
	string Value,
	ModeKind Kind,
	bool IsCurrent,
	string? ClampPeriod,
	string? ResetSummary);

/// <summary>The derived house state the modes produce: the same <c>ActiveKind</c> the engine's <see cref="Engine.ModeMonitor"/> reads.</summary>
public sealed record HouseDerivedState(ModeKind ActiveKind, bool IsAvailable);

/// <summary>The house-mode select as the Modes page shows it: its options, current value, and Normal-fallback note.</summary>
public sealed record HouseModeView(
	string Entity,
	string? CurrentValue,
	IReadOnlyList<HouseModeOptionView> Options,
	bool NoNormalOption,
	string? NormalFallbackValue,
	bool IsAvailable);

/// <summary>The engine's master switch as the dashboard shows it, with whether adaptive heating is commanding.</summary>
/// <remarks>A read-only projection; it adds no write path. The one write is <see cref="ModeService.Toggle"/>.</remarks>
public sealed record MasterSwitchView(
	ModeToggle Toggle,
	bool AdaptiveLightingOn,
	bool IsAvailable,
	bool IsReady);

/// <summary>One watched person, for the first-run board's people.</summary>
public sealed record PersonView(string EntityId, string Name, bool IsHome, bool IsAvailable);

/// <summary>One room the document names, as the dashboard counts it.</summary>
public sealed record RoomView(string? AreaId, string Name, bool IsEnabled);

/// <summary>Reads and flips the house mode entities named in the configuration document.</summary>
// The UI's whole write surface. Every entity id is re-resolved from the document, never taken from a caller.
public sealed class ModeService
{
	/// <summary>Domains this service will call <c>turn_on</c>/<c>turn_off</c> on; a <c>binary_sensor</c> can be read but never written.</summary>
	private static readonly string[] ToggleableDomains = ["input_boolean", "switch", "light", "fan"];

	// Both match Engine.PresenceMonitor; drift shows different people from the ones the add-on watches.
	private const string PersonDomain = "person";
	private const string HomeState = "home";

	// Asked on every read: the add-on's model of the house comes and goes with the connection.
	private readonly Func<IHaContext?> _ha;
	private readonly HaCatalog _catalog;
	private readonly HeatingDocumentHost _engine;
	private readonly ILogger<ModeService> _logger;

	/// <summary>Whether Home Assistant's state cache answered the last time <see cref="GetToggles"/> asked.</summary>
	// Kestrel serves before NetDaemon connects, so false is a state to render, never an error.
	public bool IsHomeAssistantReady { get; private set; } = true;

	/// <summary>Creates the service over the document host the page writes through.</summary>
	public ModeService(Func<IHaContext?> ha, HaCatalog catalog, HeatingDocumentHost engine, ILogger<ModeService> logger)
	{
		ArgumentNullException.ThrowIfNull(engine);

		_ha = ha ?? throw new ArgumentNullException(nameof(ha));
		_catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
		_engine = engine;
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <summary>The live configuration document, as the host last read or wrote it.</summary>
	private HeatingConfig Config => _engine.Current;

	/// <summary>The engine's built-in enable switch, the kill switch when the document names none.</summary>
	private string? DefaultKillSwitch => _engine.DefaultGateEntity;

	/// <summary>The modes this host has configured, with their current state; an unconfigured mode is absent, never disabled.</summary>
	public IReadOnlyList<ModeToggle> GetToggles()
	{
		var toggles = new List<ModeToggle>(1);
		GlobalConfig global = Config.Global;

		// One method owns the reset of IsHomeAssistantReady to true: GetHouseMode when a select is configured, this
		// one when there is none. Reset from both and a real disconnection is masked; from neither and a stale
		// false never clears.
		if (global.HouseMode?.Entity is not { Length: > 0 })
			IsHomeAssistantReady = true;

		// A blank GateEntity defaults to the add-on's own enable switch, with the polarity forced to
		// enabled-flag while defaulted.
		string? builtIn = DefaultKillSwitch;

		if (global.EffectiveGateEntity(builtIn) is { Length: > 0 } killSwitch)
		{
			bool defaulted = global.GateIsDefaulted(builtIn);
			bool enabledFlag = defaulted || global.GateActiveWhenOff;

			bool engineEnabled = !ModeMonitor.KillSwitchPauses(global, builtIn, TryGetState(killSwitch));

			toggles.Add(Build(
				"Adaptive Heating",
				defaulted
					? "The master switch. Off pauses all automatic heating."
					: enabledFlag
						? "The master switch. Off pauses all automatic heating."
						: "The master switch, read inverted: on means paused.",
				killSwitch,
				engineEnabled ? "Running — rooms are heated automatically." : "Paused — no thermostat will change."));
		}

		return toggles;
	}

	/// <summary>The engine's master switch, projected for the dashboard, or <c>null</c> before the built-in default resolves.</summary>
	// AdaptiveLightingOn folds the polarity in: an enabled flag reads on to command, a kill switch inverted.
	public MasterSwitchView? GetMasterSwitch()
	{
		ModeToggle? toggle = GetToggles().FirstOrDefault();
		if (toggle is null)
			return null;

		bool commanding = !ModeMonitor.KillSwitchPauses(Config.Global, DefaultKillSwitch, TryGetState(toggle.EntityId));

		return new MasterSwitchView(toggle, commanding, toggle.IsAvailable, IsHomeAssistantReady);
	}

	/// <summary>The house-mode select's live options and current value, or <c>null</c> when none is configured.</summary>
	/// <remarks>The options are the live ones unioned with the configured, so a disconnected HA cannot blank a known one.</remarks>
	public HouseModeView? GetHouseMode()
	{
		HouseModeConfig? houseMode = Config.Global.HouseMode;
		if (houseMode?.Entity is not { Length: > 0 } entity)
			return null;

		IsHomeAssistantReady = true;

		EntityState? state = TryGetState(entity);
		var current = state.AsUsableState();

		var values = _catalog.SelectOptionsOf(entity)
			.Concat(houseMode.Options.Select(option => option.Value))
			.Where(value => !string.IsNullOrWhiteSpace(value))
			.Select(value => value.Trim())
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		var options = values
			.Select(value =>
			{
				HouseModeOptionConfig? option = houseMode.OptionFor(value);
				ModeKind kind = option?.Kind ?? ModeKind.Normal;

				var clamp = kind == ModeKind.Sleep
					? HouseModeConfig.SleepClampPeriodFor(option ?? new HouseModeOptionConfig { Value = value }, Config.Periods)?.Name
					: null;
				var resetSummary = kind == ModeKind.Normal ? null : ResetSummary(option, houseMode, Config.Periods);

				return new HouseModeOptionView(
					value,
					kind,
					current is not null && string.Equals(value, current, StringComparison.OrdinalIgnoreCase),
					clamp,
					resetSummary);
			})
			.ToList();

		// No Normal option means no reset target at all; the engine no-ops instead of falling back to a tagged one.
		// Sourced from NormalOption so the banner, the cards and the engine agree.
		var noNormal = values.Count > 0 && houseMode.NormalOption is null;

		return new HouseModeView(
			entity,
			current,
			options,
			noNormal,
			houseMode.NormalOption?.Value,
			IsAvailable: state is not null);
	}

	private const string StaysUntilSwitchedBack = "stays until you switch the house back yourself";

	private static string ResetSummary(
		HouseModeOptionConfig? option,
		HouseModeConfig houseMode,
		IReadOnlyList<TimePeriodConfig> periods)
	{
		// Asked off the trigger fields, so an early return builds no sentence it throws away. This runs per option
		// on a page that ticks once a second.
		string? periodId = Blank(option?.ResetOnPeriodStartId);
		var onPresence = option?.ResetOnPresence == true;

		if (periodId is null && !onPresence)
			return StaysUntilSwitchedBack;

		// ModeAuthority.Dormant counts these same triggers as stood down, so describing them as live would have the
		// card and the dormant-rules notice contradict each other on one page.
		if (houseMode.HomeAssistantDecides)
			return $"{StaysUntilSwitchedBack} — its automatic reset rules are paused while Home Assistant decides the mode";

		var parts = new List<string>(2);

		if (periodId is not null)
		{
			string named = periods.ByKey(periodId)?.Name ?? periodId;

			parts.Add($"when '{named}' starts");
		}

		if (onPresence)
		{
			var where = option!.ResetPresenceSensors.Count > 0
				? $"{option.ResetPresenceSensors.Count} chosen {(option.ResetPresenceSensors.Count == 1 ? "sensor" : "sensors")}"
				: "any room's motion";
			parts.Add($"on presence ({where}, after {option.ResetPresenceGraceMinutes} min)");
		}

		// With no Normal option the engine has nothing to reset to and the trigger will not fire.
		return houseMode.NormalOption?.Value is { Length: > 0 } normal
			? $"switches back to {normal} {string.Join("; ", parts)}"
			: $"would switch back {string.Join("; ", parts)}, but no option is marked Normal — fix this under Configuration → House modes";
	}

	private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

	/// <summary>The derived house state the live hero shows.</summary>
	// Only downgrades IsHomeAssistantReady, so a call after GetHouseMode cannot mask a disconnection.
	public HouseDerivedState GetHouseState()
	{
		HouseModeConfig? houseMode = Config.Global.HouseMode;

		HouseModeOptionConfig? currentOption = null;
		if (houseMode?.Entity is { Length: > 0 } entity)
			currentOption = houseMode.OptionFor(TryGetState(entity).AsUsableState());

		return new HouseDerivedState(currentOption?.Kind ?? ModeKind.Normal, IsHomeAssistantReady);
	}

	/// <summary>The people the add-on watches: the configured persons when non-empty, otherwise the whole <c>person</c> domain.</summary>
	/// <remarks>Mirrors <see cref="AdaptiveLighting.Engine.PresenceMonitor"/>. Only downgrades readiness, never resets it.</remarks>
	public IReadOnlyList<PersonView> GetPeople()
	{
		IReadOnlyList<string> entityIds;

		if (Config.Global.Persons.Count > 0)
		{
			entityIds = Config.Global.Persons;
		}
		else
		{
			try
			{
				entityIds = Ha.EntityIdsInDomain(PersonDomain);
			}
			catch (InvalidOperationException)
			{
				// The registry is not up yet. An empty list renders the board's "nobody counted" state.
				IsHomeAssistantReady = false;
				return [];
			}
		}

		List<PersonView> people = new(entityIds.Count);

		foreach (string entityId in entityIds)
		{
			EntityState? state = TryGetState(entityId);
			people.Add(new PersonView(
				entityId,
				_catalog.FriendlyNameOrId(entityId),
				state.StateIs(HomeState),
				state is not null));
		}

		return people;
	}

	/// <summary>The rooms the document names, in document order, so a room a disconnected Home Assistant never reported still counts.</summary>
	public IReadOnlyList<RoomView> GetRooms()
	{
		HeatingConfig config = Config;

		return
		[
			.. config.Areas.Select(area => new RoomView(
				area.AreaId,
				AreaNaming.DisplayName(area, _catalog.AreaRegistry),
				AreaView.IsEnabled(area, config.Defaults)))
		];
	}

	/// <summary>Sets the house mode, re-resolving the entity from config and checking the option against the select's live options.</summary>
	/// <returns><c>true</c> when the call was dispatched; <c>false</c> when refused or HA is not connected.</returns>
	public bool SelectHouseMode(string option)
	{
		ArgumentNullException.ThrowIfNull(option);

		if (Config.Global.HouseMode?.Entity is not { Length: > 0 } entity)
		{
			_logger.LogWarning("Refused to set the house mode: no HouseMode.Entity is configured.");
			return false;
		}

		if (!string.Equals(entity.Domain(), "input_select", StringComparison.Ordinal))
		{
			_logger.LogWarning("Refused to set the house mode: {Entity} is not an input_select.", entity);
			return false;
		}

		if (!_catalog.SelectOptionsOf(entity).Any(live => live.SameName(option)))
		{
			_logger.LogWarning("Refused to set the house mode to '{Option}': it is not one of {Entity}'s live options.", option, entity);
			return false;
		}

		try
		{
			Ha.CallService("input_select", "select_option", ServiceTarget.FromEntity(entity), new { option = option.Trim() });
			_logger.LogInformation("Set the house mode {Entity} to {Option} from the heating web UI.", entity, option);
			return true;
		}
		catch (InvalidOperationException exception)
		{
			_logger.LogWarning(exception, "Could not set the house mode {Entity}: no connection to Home Assistant.", entity);
			return false;
		}
	}

	public bool Toggle(ModeToggle toggle)
	{
		ArgumentNullException.ThrowIfNull(toggle);

		// The argument came from a browser, so the id is re-resolved against the config and never trusted.
		if (!IsConfiguredModeEntity(toggle.EntityId))
		{
			_logger.LogWarning(
				"Refused to toggle {EntityId}: it is not one of this host's configured mode entities.",
				toggle.EntityId);
			return false;
		}

		var domain = toggle.EntityId.Domain() ?? "";
		if (!ToggleableDomains.Contains(domain, StringComparer.Ordinal))
		{
			_logger.LogWarning("Refused to toggle {EntityId}: domain {Domain} has nothing to turn on.", toggle.EntityId, domain);
			return false;
		}

		try
		{
			Ha.CallService(
				domain,
				toggle.IsOn ? "turn_off" : "turn_on",
				ServiceTarget.FromEntity(toggle.EntityId));

			_logger.LogInformation(
				"Turned {EntityId} {Direction} from the heating web UI.", toggle.EntityId, toggle.IsOn ? "off" : "on");
			return true;
		}
		catch (InvalidOperationException exception)
		{
			_logger.LogWarning(exception, "Could not toggle {EntityId}: no connection to Home Assistant.", toggle.EntityId);
			return false;
		}
	}

	private bool IsConfiguredModeEntity(string entityId) =>
		string.Equals(entityId, Config.Global.EffectiveGateEntity(DefaultKillSwitch), StringComparison.Ordinal);

	// IHaContext.GetState throws until NetDaemon's initial connection completes, which is no state, not a fault.
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

	private bool IsOn(string? entityId) =>
		!string.IsNullOrWhiteSpace(entityId) && TryGetState(entityId).StateIs("on");

	private ModeToggle Build(string label, string description, string entityId, string meaning) =>
		new(
			label,
			description,
			entityId,
			IsOn(entityId),
			TryGetState(entityId) is not null,
			ToggleableDomains.Contains(entityId.Domain() ?? "", StringComparer.Ordinal),
			meaning);

	private IHaContext Ha => _ha() ?? throw new InvalidOperationException("Home Assistant has not answered yet.");
}
