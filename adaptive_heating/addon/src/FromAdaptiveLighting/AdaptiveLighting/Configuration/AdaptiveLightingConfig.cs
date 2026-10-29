using YamlDotNet.Serialization;

namespace Adaptive.Settings;

/// <summary>
///     Root of the adaptive heating configuration document. One per add-on, bound from a YAML file whose
///     top-level key is the fully qualified name of this class.
/// </summary>
public class HeatingConfig
{
	public string? ConfigName { get; set; }

	public GlobalConfig Global { get; set; } = new();

	/// <summary>Baseline for every per-area knob. A <see cref="AreaConfig"/> overrides only what differs.</summary>
	public AreaSettings Defaults { get; set; } = new();

	/// <summary>The house-wide period table. Ordered by <see cref="TimePeriodConfig.Start"/> at resolution time, not here.</summary>
	public List<TimePeriodConfig> Periods { get; set; } = [];

	/// <summary>Areas are opt-in: a Home Assistant area absent from this list is never touched.</summary>
	public List<AreaConfig> Areas { get; set; } = [];

	/// <summary>How many areas the engine is meant to be commanding, which is the denominator of "n of m running".</summary>
	[YamlIgnore]
	public int ManagedAreaCount => Areas.Count(area => area.Effective(Defaults).Enabled);

	/// <summary>The document a fresh installation starts from: no entity named, no room, no period.</summary>
	public static HeatingConfig CreateDefault() =>
		new()
		{
			ConfigName = "Adaptive Heating",
			Global = new GlobalConfig(),
			Defaults = new AreaSettings(),
			Periods = [],
			Areas = [],
		};
}

/// <summary>Settings that apply to the whole house, not to a single area.</summary>
public class GlobalConfig
{
	/// <summary><c>person.*</c> / <c>device_tracker.*</c> ids to watch. Empty means "discover every person entity".</summary>
	public List<string> Persons { get; set; } = [];

	/// <summary>Entity gating the whole engine; <c>null</c> means the built-in switch. Read through <see cref="EffectiveGateEntity"/>, never directly.</summary>
	public string? GateEntity { get; set; }

	/// <summary>When <c>true</c> the switch is an enabled flag, so <c>off</c> stops the engine commanding.</summary>
	public bool GateActiveWhenOff { get; set; } = true;

	/// <summary>The gate actually read: this document's own, else <paramref name="defaultGateEntity"/>.</summary>
	// The default is the add-on's own switch, known only to the host, so it is passed in and never stored here.
	public string? EffectiveGateEntity(string? defaultGateEntity) =>
		GateEntity is { Length: > 0 } ? GateEntity : defaultGateEntity;

	/// <summary>Whether the effective gate is the built-in default, not an operator's own entity.</summary>
	/// <remarks>While defaulted the switch is always an enabled flag, whatever <see cref="GateActiveWhenOff"/> says.</remarks>
	public bool GateIsDefaulted(string? defaultGateEntity) =>
		string.IsNullOrWhiteSpace(GateEntity) && defaultGateEntity is { Length: > 0 };

	public HouseModeConfig? HouseMode { get; set; }

	public PeriodSelectConfig? PeriodSelect { get; set; }

	/// <summary>The house's outdoor temperature sensor. One house value: the same entity id goes to every room.</summary>
	public string? OutdoorSensor { get; set; }

	/// <summary>The weather entity a warm-up's forecast is read from.</summary>
	public string? ForecastEntity { get; set; }

	/// <summary>Which reading a warm-up is planned against.</summary>
	public WarmUpSource WarmUpSource { get; set; } = WarmUpSource.Forecast;

	/// <summary>The calendar the planned arrivals are read from, by entity id, or <c>null</c> where none is chosen.</summary>
	public string? ArrivalCalendar { get; set; }

	/// <summary>The calendar each finished warm-up is written to, by entity id, or <c>null</c> where none is chosen.</summary>
	public string? RecordCalendar { get; set; }

	/// <summary>How often a room re-evaluates its target. A pass that finds nothing changed commands nothing.</summary>
	public int PassCadenceSeconds { get; set; } = 60;

	/// <summary>How long after a command a state change on the same room is still read as the engine's own.</summary>
	public int SelfEchoWindowSeconds { get; set; } = 8;

	/// <summary>Whether a change carrying a parent context (another automation) counts as a hand.</summary>
	public bool TreatAutomationsAsManual { get; set; } = true;

	/// <summary>The minimum log level, one of <see cref="LogLevelSetting.Accepted"/>; <c>null</c> keeps the host's own.</summary>
	/// <remarks>Any other text is a warning and is ignored.</remarks>
	public string? LogLevel { get; set; }

	/// <summary>The exclude label a document gets when nothing says otherwise.</summary>
	// Underscores, so the value can be the label id Home Assistant generates: it slugs a hyphen to an underscore,
	// and a hyphenated default could only ever match by name.
	public const string DefaultExcludeLabel = "adaptive_exclude";

	/// <summary>The discovery label a document gets when nothing says otherwise.</summary>
	public const string DefaultIncludeLabel = "heating";

	/// <summary>Registry label marking an entity the engine must never touch.</summary>
	public string ExcludeLabel { get; set; } = DefaultExcludeLabel;

	/// <summary>
	///     Registry label a heater or a temperature sensor must carry to be discovered; <c>null</c> discovers every
	///     candidate. The exclude label always wins over it.
	/// </summary>
	public string? IncludeLabel { get; set; } = DefaultIncludeLabel;

	/// <summary>How long the outdoor sensor may go without reporting before the engine stops believing it.</summary>
	/// <remarks>Zero or less switches the rule off.</remarks>
	public int OutdoorSensorStaleAfterMinutes { get; set; } = 120;

	/// <summary>Origins allowed to show this UI inside a frame. Empty leaves the framework's refusal untouched.</summary>
	public List<string> EmbedFrom { get; set; } = [];

	/// <summary>The house's sun entity: what every area's darkness and boundary checks read the sun's height and times from.</summary>
	/// <remarks>House-wide, not a per-area override: almost every Home Assistant install has exactly one.</remarks>
	public string SunEntity { get; set; } = "sun.sun";

	/// <summary>Whether the rooms have ever been proposed from the area registry.</summary>
	// Once set, never scanned again: a household that removes every room must find them still gone next start.
	public bool AreasAutoDiscovered { get; set; }

	public int AwayDebounceMinutes { get; set; } = 5;

	/// <summary>Whether every thermostat command is written to a file per thermostat under <c>log/commands</c> beside the document.</summary>
	public bool CommandLog { get; set; }
}

/// <summary>
///     Every knob an area can carry. <see cref="HeatingConfig.Defaults"/> supplies the baseline and
///     <see cref="AreaConfig"/> merges its nullable twins over it.
/// </summary>
public class AreaSettings
{
	/// <summary>How long a change made at the thermostat holds the room.</summary>
	public int OverrideDurationMinutes { get; set; } = 120;

	public double LowestNightTemperature { get; set; } = 15.0;

	public double FrostTemperature { get; set; } = 5.0;

	// Off is no frost limit. A bool, since the writer omits nulls and a null would load back as 5 °C.
	public bool FrostProtection { get; set; } = true;

	/// <summary>Whether the engine commands this area at all. A disabled area is still observed and published.</summary>
	public bool Enabled { get; set; } = true;
}

/// <summary>Which reading a warm-up's deadline is planned against.</summary>
/// <remarks>Ordinals pinned: an unknown key is silence in the binder, while an unknown enum value is a parse failure.</remarks>
public enum WarmUpSource
{
	/// <summary>The weather entity's forecast, and the default: a warm-up is then planned ahead.</summary>
	Forecast = 0,

	/// <summary>The house's outdoor temperature sensor as it reads now.</summary>
	OutdoorMeasurement = 1
}
