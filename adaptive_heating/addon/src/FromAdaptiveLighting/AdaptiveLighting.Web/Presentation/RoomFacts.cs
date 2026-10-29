using System.Text.Json;

using AdaptiveHeating.Page.Settings;

using AdaptiveLighting.Abstractions;

using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Web.Presentation;

/// <summary>One line of the room page's evidence table.</summary>
public sealed record RoomFact(string Label, string Value, string? Title = null, string? Detail = null, bool IsProse = false);

public enum SensorWarningKind
{
	Quiet,
	Stuck,
	FarFromTheOthers,
	Disagreeing
}

/// <summary>A sensor of the room that is silent: the badge's few words, the sensors' names, and what it means.</summary>
public sealed record SensorWarning(SensorWarningKind Kind, string Text, IReadOnlyList<string> Names, string Advice);

/// <summary>What the room's own thermostat and reason sensor publish, read off their states.</summary>
/// <param name="Reason">The reason sensor's state: one of the integration's reason codes, or <c>null</c> while heating.</param>
/// <param name="LeftOut">Each sensor the reading leaves out, with the integration's word for why.</param>
public sealed record RoomCard(
	double? Temperature,
	double? Target,
	bool HeatingNow,
	bool SwitchedOff,
	string? Reason,
	string? BorrowedFrom,
	int RestingOn,
	IReadOnlyDictionary<string, string> LeftOut,
	string? Preset,
	DateTimeOffset? HoldEndsAt,
	DateTimeOffset? WarmBy,
	string? Fault,
	bool NoReadingAtAll,
	string? Switched,
	DateTimeOffset? TargetSetAt,
	DateTimeOffset ReadAt)
{
	/// <summary>How many heaters the room drives, as the integration counts them.</summary>
	public int? HeaterCount { get; init; }

	/// <summary>How many of them did not answer at the integration's last evaluation.</summary>
	public int? HeatersNotResponding { get; init; }

	/// <summary>The card, or <c>null</c> where the thermostat has not reported.</summary>
	public static RoomCard? From(EntityState? thermostat, EntityState? reason, DateTimeOffset now)
	{
		if (thermostat is null || thermostat.State is null or "unavailable" or "unknown")
			return null;

		return new RoomCard(
			thermostat.AttrDouble("current_temperature"),
			thermostat.AttrDouble("temperature"),
			string.Equals(thermostat.AttrString("hvac_action"), "heating", StringComparison.Ordinal),
			string.Equals(thermostat.State, "off", StringComparison.Ordinal),
			reason?.State is { Length: > 0 } code and not ("unknown" or "unavailable") ? code : null,
			thermostat.AttrString("reading_borrowed_from"),
			(int)(thermostat.AttrDouble("reading_rests_on") ?? 0),
			LeftOutOf(thermostat),
			thermostat.AttrString("preset_mode"),
			thermostat.AttrDateTimeOffset("hold_ends_at"),
			WarmUpDeadline(thermostat),
			thermostat.AttrString("fault"),
			Flag(thermostat, "no_reading_at_all"),
			thermostat.AttrString("switched"),
			thermostat.AttrDateTimeOffset("target_set_at"),
			thermostat.LastUpdated is { } updated ? new DateTimeOffset(DateTime.SpecifyKind(updated, DateTimeKind.Utc)) : now)
		{
			HeaterCount = (int?)thermostat.AttrDouble("heater_count"),
			HeatersNotResponding = (int?)thermostat.AttrDouble("heaters_not_responding")
		};
	}

	private static JsonElement? Attribute(EntityState state, string name) =>
		state.Attributes is IReadOnlyDictionary<string, object> attributes
		&& attributes.TryGetValue(name, out object? value)
		&& value is JsonElement element
			? element
			: null;

	private static bool Flag(EntityState state, string name) =>
		Attribute(state, name) is { ValueKind: JsonValueKind.True };

	private static Dictionary<string, string> LeftOutOf(EntityState state)
	{
		Dictionary<string, string> leftOut = new(StringComparer.Ordinal);

		if (Attribute(state, "sensors_left_out") is { ValueKind: JsonValueKind.Object } held)
			foreach (JsonProperty sensor in held.EnumerateObject())
				if (sensor.Value.ValueKind == JsonValueKind.String)
					leftOut[sensor.Name] = sensor.Value.GetString()!;

		return leftOut;
	}

	private static DateTimeOffset? WarmUpDeadline(EntityState state) =>
		Attribute(state, "warm_up") is { ValueKind: JsonValueKind.Object } warmUp
		&& warmUp.TryGetProperty("deadline", out JsonElement deadline)
		&& deadline.ValueKind == JsonValueKind.String
		&& DateTimeOffset.TryParse(deadline.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset at)
			? at
			: null;
}

/// <summary>What the room page says about a room now: the present-tense line, what happens next, and what the room saw.</summary>
// Read off the card and the verdict, never worked out again: only they know which sensors and gates were consulted.
public static class RoomFacts
{
	/// <summary>The integration's reason codes, in the words its own strings file gives them.</summary>
	public static string ReasonWords(string reason) => reason switch
	{
		"room_is_switched_off" => "Switched off",
		"window_is_open" => "Window open",
		"no_temperature_reading" => "No reading",
		"at_temperature" => "At temperature",
		"pacing_the_cycle" => "Pacing the cycle",
		"relay_guard_is_holding" => "Relay held",
		"the_mode_says_off" => "Off by the mode",
		"the_heater_is_not_answering" => "Heater not answering",
		_ => reason
	};

	/// <summary>What the room saw, as the room page's evidence table.</summary>
	public static IReadOnlyList<RoomFact> For(
		RoomCard card,
		RoomVerdict? verdict,
		string? periodName,
		DateTimeOffset now,
		Func<string, string>? nameOf = null)
	{
		ArgumentNullException.ThrowIfNull(card);

		List<RoomFact> facts = [];

		if (verdict?.MasterSwitchOff is true)
		{
			facts.Add(new RoomFact(
				"Master switch",
				"off — nothing will change",
				"Adaptive Heating is switched off for the whole house."));
		}

		facts.Add(new RoomFact(
			"Heating now?",
			card.HeatingNow ? "yes" : card.Reason is { Length: > 0 } reason ? $"no — {ReasonWords(reason).ToLowerInvariant()}" : "no",
			"Whether the room's heaters are on, and the room's own reason where they are not."));

		if (verdict?.PausedBy is { Length: > 0 } pausedBy)
		{
			facts.Add(new RoomFact(
				"Paused by",
				Name(pausedBy, nameOf),
				"While this is on, the room is heated as in away.",
				IsProse: true));
		}

		if (verdict?.HeldBy is { Length: > 0 } heldBy)
		{
			facts.Add(new RoomFact(
				"Held by",
				Name(heldBy, nameOf),
				"While this is on, a new mode or period waits; a change at the thermostat still counts.",
				IsProse: true));
		}

		facts.Add(new RoomFact("Reading", Reading(card, nameOf), "What the room's temperature is read from.", LeftOutLine(card)));

		facts.Add(card.NoReadingAtAll
			? new RoomFact("Holding", card.Switched is "on" ? "on" : "off", "No reading here, so the mode switches the heaters on or off.")
			: new RoomFact("Holding", Degrees(card.Target), "The temperature the room is holding.", card.Preset));

		facts.Add(card.TargetSetAt is { } changed
			? ChangedBy(verdict, changed) is { } by
				? new RoomFact("Last changed", Stamp(changed, now), $"The temperature was last set at {Clock(changed)}.", by)
				: new RoomFact("Last changed", Stamp(changed, now), $"The temperature was last set at {Clock(changed)}, by something the add-on could not name.")
			: new RoomFact("Last changed", "not yet", "Nothing has set this room's temperature since it was set up."));

		facts.Add(new RoomFact(
			"Time of day",
			periodName is { Length: > 0 } period ? period : "nothing scheduled",
			"The schedule period this room is in. It sets the temperature."));

		return facts;
	}

	// Named only where the remembered change, or the add-on's own command, is the one the card stamps.
	private static string? ChangedBy(RoomVerdict? verdict, DateTimeOffset changed) =>
		verdict?.History is { ChangedAt: { } at, ChangedBy: { Length: > 0 } by } && (at - changed).Duration() <= TimeSpan.FromSeconds(2)
			? by
			: verdict?.CommandedAt is { } commanded && changed >= commanded && changed - commanded <= OwnCommandLands
				? ChangeOriginNames.ByTheEngine
				: null;

	// Lighting's default echo window: the integration stamps the target when the add-on's command arrives.
	private static readonly TimeSpan OwnCommandLands = TimeSpan.FromSeconds(8);

	/// <summary>The room's present tense: what the heaters are doing and why.</summary>
	public static string Headline(RoomCard card, RoomVerdict? verdict)
	{
		ArgumentNullException.ThrowIfNull(card);

		return (card, verdict) switch
		{
			(_, { MasterSwitchOff: true }) => "Paused by the master switch",
			(_, { Enabled: false }) => "Never changes by itself",
			(_, { PausedBy: { Length: > 0 } }) => "Paused — heated as in away",
			({ NoReadingAtAll: true, Switched: "on" }, _) => "Switched on by the mode",
			({ NoReadingAtAll: true }, _) => "Off by the mode",
			({ HeatingNow: true }, _) => $"Heating to {Degrees(card.Target)}",
			({ Reason: "at_temperature" }, _) => $"Holding {Degrees(card.Target)}",
			({ Reason: { Length: > 0 } reason }, _) => ReasonWords(reason),
			_ => $"Holding {Degrees(card.Target)}"
		};
	}

	/// <summary>What happens next, or <c>null</c> when nothing is pending.</summary>
	public static string? NextLine(RoomCard card, RoomVerdict? verdict, DateTimeOffset now, Func<string, string>? nameOf = null)
	{
		ArgumentNullException.ThrowIfNull(card);

		if (verdict?.HeldBy is { Length: > 0 } holder)
			return $"Held by {Name(holder, nameOf)}";

		if (card.WarmBy is { } warmBy && warmBy > now)
			return $"Warm by {warmBy.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture)}";

		return card.HoldEndsAt is { } ends && ends > now ? $"Back to the schedule {In(ends, now)}" : null;
	}

	/// <summary>How many of the room's heaters have stopped answering, or <c>null</c> while every one answers.</summary>
	/// <remarks>Read off the count the integration published, never off Home Assistant.</remarks>
	public static string? NotResponding(RoomCard card)
	{
		ArgumentNullException.ThrowIfNull(card);

		return card switch
		{
			{ HeatersNotResponding: not > 0 } => null,
			{ HeaterCount: 1 } => "This room's heater is not answering.",
			{ HeatersNotResponding: 1, HeaterCount: { } total } => $"1 of {total} heaters is not answering.",
			{ HeatersNotResponding: { } missing, HeaterCount: { } total } => $"{missing} of {total} heaters are not answering.",
			{ HeatersNotResponding: 1 } => "1 heater is not answering.",
			{ HeatersNotResponding: { } missing } => $"{missing} heaters are not answering."
		};
	}

	/// <summary>What the room's power meter says is wrong, or <c>null</c> while nothing is.</summary>
	public static string? Fault(RoomCard card)
	{
		ArgumentNullException.ThrowIfNull(card);

		return card.Fault switch
		{
			"heater_drew_nothing" => "Heater drawing nothing",
			"relay_stuck_closed" => "Relay stuck closed",
			null or "" => null,
			{ } other => other
		};
	}

	/// <summary>The room's sensors the reading leaves out, one entry per kind.</summary>
	public static IReadOnlyList<SensorWarning> SensorWarnings(RoomCard card, Func<string, string> nameOf)
	{
		ArgumentNullException.ThrowIfNull(card);
		ArgumentNullException.ThrowIfNull(nameOf);

		return
		[
			.. card.LeftOut
				.GroupBy(sensor => KindOf(sensor.Value))
				.OrderBy(kind => kind.Key)
				.Select(kind => SensorWarningFor(kind.Key, [.. kind.Select(sensor => nameOf(sensor.Key))]))
				.OfType<SensorWarning>()
		];
	}

	/// <summary>The wording for a room's left-out sensors; <c>null</c> when none is named.</summary>
	public static SensorWarning? SensorWarningFor(SensorWarningKind kind, IReadOnlyList<string> names)
	{
		ArgumentNullException.ThrowIfNull(names);

		if (names.Count == 0)
			return null;

		string many = names.Count == 1 ? "Temperature sensor" : $"{names.Count} temperature sensors";

		return kind switch
		{
			SensorWarningKind.Quiet => new SensorWarning(kind, $"{many} quiet", names, "The room reads the sensors that still report."),
			SensorWarningKind.Stuck => new SensorWarning(kind, $"{many} stuck", names, "A reading that never moves is left out of the room's temperature."),
			SensorWarningKind.FarFromTheOthers => new SensorWarning(kind, $"{many} far off", names, "A reading far from the room's other sensors is left out."),
			_ => new SensorWarning(kind, $"{many} disagree", names, "Two sensors that disagree are both left out until one moves back.")
		};
	}

	/// <summary>One sentence per temperature sensor whose battery reads low, empty while none does.</summary>
	public static IReadOnlyList<string> LowBatteries(IReadOnlyList<SensorBattery> low, Func<string, string> nameOf)
	{
		ArgumentNullException.ThrowIfNull(low);
		ArgumentNullException.ThrowIfNull(nameOf);

		return [.. low.Select(battery => battery.LevelPct is { } level
			? $"{nameOf(battery.SensorId)} battery at {level:0} %"
			: $"{nameOf(battery.SensorId)} battery low")];
	}

	/// <summary>When the room last reported, as the header's stamp.</summary>
	public static string Since(RoomCard card, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(card);

		return $"since {Clock(card.ReadAt)} · {Ago(card.ReadAt, now)}";
	}

	public static string Ago(DateTimeOffset at, DateTimeOffset now)
	{
		TimeSpan span = now - at;

		if (span < TimeSpan.FromSeconds(10))
			return "just now";
		if (span < TimeSpan.FromMinutes(1))
			return $"{(int)span.TotalSeconds} s ago";
		if (span < TimeSpan.FromHours(1))
			return $"{(int)span.TotalMinutes} min ago";
		if (span < TimeSpan.FromDays(1))
			return span.Minutes == 0 ? $"{(int)span.TotalHours} h ago" : $"{(int)span.TotalHours} h {span.Minutes} min ago";

		return $"{(int)span.TotalDays} d ago";
	}

	/// <summary>Relative future time, never negative.</summary>
	public static string In(DateTimeOffset due, DateTimeOffset now)
	{
		TimeSpan span = due - now;

		if (span <= TimeSpan.Zero)
			return "any moment now";
		if (span < TimeSpan.FromMinutes(2))
			return $"in {(int)span.TotalSeconds} s";
		if (span < TimeSpan.FromHours(1))
			return $"in {(int)span.TotalMinutes} min";

		return span.Minutes == 0 ? $"in {(int)span.TotalHours} h" : $"in {(int)span.TotalHours} h {span.Minutes} min";
	}

	/// <summary>A clock time in the reader's own zone, seconds included, which the log rows need.</summary>
	public static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);

	/// <summary>A past moment as the table writes it: how long ago first, the clock time second.</summary>
	private static string Stamp(DateTimeOffset at, DateTimeOffset now) =>
		$"{Ago(at, now)} · {at.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture)}";

	// The integration's own exclusion words, from its sensors module.
	private static SensorWarningKind KindOf(string excluded) => excluded switch
	{
		"quiet" => SensorWarningKind.Quiet,
		"stuck" => SensorWarningKind.Stuck,
		"far_from_the_others" => SensorWarningKind.FarFromTheOthers,
		_ => SensorWarningKind.Disagreeing
	};

	private static string Reading(RoomCard card, Func<string, string>? nameOf)
	{
		if (card.Temperature is not { } temperature)
			return "no reading";

		if (card.BorrowedFrom is { Length: > 0 } lender)
			return $"{Degrees(temperature)} · borrowed from {Name(lender, nameOf)}";

		return card.RestingOn == 1 ? $"{Degrees(temperature)} · 1 sensor" : $"{Degrees(temperature)} · {card.RestingOn} sensors";
	}

	private static string? LeftOutLine(RoomCard card) =>
		card.LeftOut.Count switch
		{
			0 => null,
			1 => "1 sensor left out",
			int count => $"{count} sensors left out"
		};

	private static string Name(string entityId, Func<string, string>? nameOf) =>
		nameOf?.Invoke(entityId) is { Length: > 0 } friendly ? friendly : entityId;

	private static string Degrees(double? celsius) =>
		celsius is { } value ? $"{value.ToString("0.#", CultureInfo.CurrentCulture)} °C" : "not set";
}
