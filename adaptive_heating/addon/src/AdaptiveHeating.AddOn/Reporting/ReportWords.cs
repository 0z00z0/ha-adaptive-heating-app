using System.Globalization;

using AdaptiveHeating.Planner;

namespace AdaptiveHeating.AddOn.Reporting;

/// <summary>One report as words: the headline, and the condition behind it.</summary>
internal sealed record ActivityLine(string What, string Why);

/// <summary>Turns a report into the words a person reads, and is the only place that does.</summary>
/// <remarks>
///     Taken in shape from the lighting engine's <c>ActivityView.Describe</c>, read on 2026-09-25: one line per
///     report, a headline and a condition. Its sentences could not come with it, because every one of them names a
///     lighting concept.
///     <para>
///         Numbers are written invariantly. The same words go into a log file read on a machine of unknown locale
///         and onto a Home Assistant card, and the settings page has its own translated words and reads none of this.
///     </para>
/// </remarks>
internal static class ReportWords
{
	/// <summary>The headline and the condition for one report.</summary>
	/// <param name="report">What happened, with the machine values the journal keeps.</param>
	/// <param name="roomName">What a person calls the room, or the words for the house as a whole.</param>
	/// <param name="household">The household's clock, which every time in a line is rendered on.</param>
	public static ActivityLine Describe(HeatingReport report, string roomName, TheHouseholdClock household)
	{
		ArgumentNullException.ThrowIfNull(report);
		ArgumentNullException.ThrowIfNull(household);

		return report.What switch
		{
			WhatHappened.Started =>
				new ActivityLine("Heating started", report.Named ?? ""),

			WhatHappened.ModeInForce =>
				new ActivityLine($"Mode: {ModeWords(report.Named)}", ""),

			WhatHappened.RoomHolds =>
				new ActivityLine($"{roomName} holds {Degrees(report.Temperature)}", From(SourceWords(report.Named))),

			WhatHappened.RoomSwitched =>
				new ActivityLine($"{roomName} switched {(report.SwitchedOn == true ? "on" : "off")}", "No reading in that room"),

			WhatHappened.WarmUpStarted =>
				new ActivityLine($"{roomName} warming to {Degrees(report.Temperature)}", By(report.Deadline, household)),

			WhatHappened.CannotWarmInTime =>
				new ActivityLine(
					$"{roomName} cannot reach {Degrees(report.Temperature)}",
					$"Reaches {Degrees(report.Against)} {By(report.Deadline, household)}".Trim()),

			WhatHappened.WarmingRateLearnt =>
				new ActivityLine($"{roomName} warms at {Rate(report.DegreesPerHour)}", Outside(report.Against)),

			WhatHappened.RoseWithoutHeating =>
				new ActivityLine($"{roomName} rose unheated", "Nothing learnt from it"),

			WhatHappened.OutdoorTermLearnt =>
				new ActivityLine($"{roomName} settled {Degrees(report.Temperature)} low", Outside(report.Against)),

			WhatHappened.ArrivalCalendarMissing =>
				new ActivityLine("Arrival calendar missing", Named(report.Named)),

			WhatHappened.WarmUpRecorded =>
				new ActivityLine($"{roomName} warm-up recorded", Reached(report.Temperature, report.Against)),

			WhatHappened.WarmUpNotRecorded =>
				new ActivityLine(
					$"{roomName} warm-up not recorded",
					report.Named is { Length: > 0 } ? Named(report.Named) : "No record calendar chosen"),

			WhatHappened.OutdoorSensorsAllFailed =>
				new ActivityLine(
					"Outdoor sensors all out",
					report.Named is { Length: > 0 } forecast ? $"Running on {forecast}" : "No forecast chosen"),

			WhatHappened.HouseholdZoneNotResolved =>
				new ActivityLine(
					"Household clock not set",
					report.Named is { Length: > 0 } given
						? $"{given} did not resolve. Times will be wrong until it is fixed."
						: "No zone was given. Times will be wrong until it is fixed."),

			WhatHappened.SettingsSaved =>
				new ActivityLine("Settings saved", "Every room follows them from the next pass"),

			WhatHappened.HandChange =>
				new ActivityLine($"{roomName} set manually to {Degrees(report.Temperature)}", report.Named ?? ""),

			WhatHappened.HandChangeEnded =>
				new ActivityLine($"The manual change in {roomName} ran its course", ""),

			WhatHappened.RoomMoved =>
				new ActivityLine($"{roomName} moved to another area", "Moved in Home Assistant"),

			// A kind no longer raised, read back from an older journal.
			_ => new ActivityLine(report.What.ToString(), "")
		};
	}

	/// <summary>The words for a mode, or <c>null</c> where this member has none of its own.</summary>
	// Returns null rather than falling through to a default, so a member added without words fails a test
	// instead of reading as another member's.
	public static string? WordsFor(HeatingMode mode) =>
		mode switch
		{
			HeatingMode.Away => "Away",
			HeatingMode.Night => "Night",
			HeatingMode.Home => "Home",
			HeatingMode.Boost => "Boost",
			_ => null
		};

	/// <summary>The words for what chose a room's temperature, or <c>null</c> where this member has none of its own.</summary>
	public static string? WordsFor(TargetSource source) =>
		source switch
		{
			TargetSource.ModeTemperature => "the mode's temperature",
			TargetSource.HandChange => "a change by hand",
			TargetSource.TimedHold => "a timed hold",
			TargetSource.WarmUp => "a warm-up",
			_ => null
		};

	// The report carries the member's own name, because the journal keeps it and a rename of the words must not
	// split one history in two. The words are looked up here, which is where every report becomes words.
	private static string ModeWords(string? named) =>
		Enum.TryParse(named, out HeatingMode mode) && WordsFor(mode) is { } words ? words : named ?? "";

	private static string SourceWords(string? named) =>
		Enum.TryParse(named, out TargetSource source) && WordsFor(source) is { } words ? words : named ?? "";

	private static string Degrees(double? value) =>
		value is { } number ? number.ToString("0.#", CultureInfo.InvariantCulture) + " °C" : "—";

	private static string Rate(double? value) =>
		value is { } number ? number.ToString("0.##", CultureInfo.InvariantCulture) + " °C/h" : "—";

	private static string Outside(double? value) =>
		value is { } number ? $"{Degrees(number)} outside" : "";

	/// <summary>What the room reached against what it was asked for, which is the whole verdict on a warm-up.</summary>
	private static string Reached(double? wanted, double? reached) =>
		reached is { } got && wanted is { } asked
			? (got + 1e-9 >= asked ? $"Reached {Degrees(got)}" : $"Short at {Degrees(got)} of {Degrees(asked)}")
			: "No reading at the deadline";

	private static string Named(string? entityId) =>
		entityId is { Length: > 0 } named ? $"{named} is not in Home Assistant" : "";

	private static string From(string? named) =>
		named is { Length: > 0 } ? $"From {named}" : "";

	// The household's wall clock, not the offset the stored instant happens to carry: a deadline recorded in UTC
	// would otherwise be read out two hours off the clock the cabin is looking at.
	private static string By(DateTimeOffset? deadline, TheHouseholdClock household) =>
		deadline is { } at ? $"by {household.Wall(at).ToString("HH:mm", CultureInfo.InvariantCulture)}" : "";
}
