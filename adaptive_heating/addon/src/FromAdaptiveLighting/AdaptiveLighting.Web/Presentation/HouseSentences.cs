using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Web.Presentation;

/// <summary>One house mode as the House tab reads it: its name, its kind, and what it does as a sentence.</summary>
public sealed record ModeLine(string Name, ModeKind Kind, IReadOnlyList<Sentence> Sentences);

/// <summary>The house's own behaviour written as prose: its modes and its idea of an empty house.</summary>
public static class HouseSentences
{
	private const string ModePrefix = "mode";

	public static IReadOnlyList<TokenChoice> AwayDebounceChoices { get; } =
		TokenChoices.DurationsInMinutes(1, 5, 10, 15, 30);

	public static IReadOnlyList<TokenChoice> AutoAwayChoices { get; } =
		TokenChoices.DurationsInMinutes(60, 120, 240, 360, 720);

	/// <summary>How long an arrival is ignored before it can end a mode.</summary>
	public static IReadOnlyList<TokenChoice> GraceChoices { get; } =
		TokenChoices.DurationsInMinutes(0, 5, 15, 30);

	public static Sentence AwayDebounce(GlobalConfig global)
	{
		ArgumentNullException.ThrowIfNull(global);

		return SentenceBuilder.Start("Show the house as empty ")
			.Duration(
				nameof(GlobalConfig.AwayDebounceMinutes),
				"Show the house as empty after",
				global.AwayDebounceMinutes * 60,
				AwayDebounceChoices)
			.Text(" after the last person leaves.")
			.Build();
	}

	/// <summary>One sentence per house-mode option: what it does, what turns it on, and what ends it.</summary>
	public static IReadOnlyList<ModeLine> Modes(HouseModeConfig? houseMode, IReadOnlyList<TimePeriodConfig> periods)
	{
		ArgumentNullException.ThrowIfNull(periods);

		if (houseMode is not { Options.Count: > 0 })
			return [];

		List<ModeLine> lines = new(houseMode.Options.Count);

		for (int index = 0; index < houseMode.Options.Count; index++)
		{
			HouseModeOptionConfig option = houseMode.Options[index];

			lines.Add(new ModeLine(Name(option), option.Kind, [Mode(option, index, periods, ReferenceEquals(option, houseMode.NormalOption))]));
		}

		return lines;
	}

	/// <summary>The token key one mode option's setting is carried under.</summary>
	public static string ModeKey(int index, string property) =>
		$"{ModePrefix}:{index.ToString(CultureInfo.InvariantCulture)}:{property}";

	/// <summary>Reads a mode token key back into the option it names and the setting it changes.</summary>
	public static bool TryReadModeKey(string? key, out int index, out string property)
	{
		// Both halves of the encoding stay here; a page that split the key itself would be a second copy.
		index = -1;
		property = string.Empty;

		if (key is null)
			return false;

		string[] parts = key.Split(':');

		if (parts.Length != 3 || !string.Equals(parts[0], ModePrefix, StringComparison.Ordinal))
			return false;

		if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
			return false;

		property = parts[2];

		return property.Length > 0;
	}

	// Kind defaults to Normal, so a list can hold several; only the first is the reset target NormalOption returns.
	private static Sentence Mode(HouseModeOptionConfig option, int index, IReadOnlyList<TimePeriodConfig> periods, bool isResetTarget)
	{
		SentenceBuilder builder = SentenceBuilder.Start();

		switch (option.Kind)
		{
			case ModeKind.Normal:
				return builder
					.Text(isResetTarget
						? "is everyday heating; other modes end here."
						: "is everyday heating; modes end at the first Normal above.")
					.Build();

			case ModeKind.Sleep:
				builder.Text(HouseModeConfig.SleepClampPeriodFor(option, periods) is { Name: { Length: > 0 } clamp }
					? $"holds every room at or below its {clamp} temperature"
					: "holds every room — no period named, nothing held");
				break;

			case ModeKind.Away:
				Arms(builder, option, index);
				builder.Text("holds every room at its away temperature");
				break;

			case ModeKind.Boost:
				builder.Text("holds every room at its boost temperature");
				break;

			case ModeKind.PlannedToArrive:
				builder.Text("holds every room at its away temperature");
				break;

			case ModeKind.Guest:
				builder.Text("holds every room, so nothing changes by itself");
				break;

			default:
				builder.Text("holds every room at its home temperature");
				break;
		}

		Ends(builder, option, index, periods);
		builder.Text(".");

		return builder.Build();
	}

	/// <summary>The clause about a mode that turns itself on. Not built at all when nothing arms it.</summary>
	private static void Arms(SentenceBuilder builder, HouseModeOptionConfig option, int index) =>
		builder.When(option.ActivateAfterNoMotionMinutes is > 0, clause => clause
			.Text("switches on by itself after the house has been still ")
			.Duration(
				ModeKey(index, nameof(HouseModeOptionConfig.ActivateAfterNoMotionMinutes)),
				"Switch to this mode after no motion for",
				option.ActivateAfterNoMotionMinutes!.Value * 60,
				AutoAwayChoices)
			.Text(", then "));

	/// <summary>The clauses about what ends a mode, joined as English joins them.</summary>
	private static void Ends(SentenceBuilder builder, HouseModeOptionConfig option, int index, IReadOnlyList<TimePeriodConfig> periods)
	{
		if (option.Kind == ModeKind.Normal)
			return;

		bool presence = option.ResetOnPresence;
		List<string> others = [];

		if (option.ResetOnPeriodStartId is { Length: > 0 } resetPeriod)
			others.Add($"when the {NameOf(periods, resetPeriod)} period starts");

		if (!presence && others.Count == 0)
		{
			builder.Text(" until you switch the house back yourself");
			return;
		}

		builder.Text(". It ends ");

		if (presence)
		{
			builder.Text("when someone comes home — ignoring the first ")
				.Duration(
					ModeKey(index, nameof(HouseModeOptionConfig.ResetPresenceGraceMinutes)),
					"Ignore arrivals for",
					Math.Max(0, option.ResetPresenceGraceMinutes) * 60,
					GraceChoices)
				.Text(" so your own leaving does not cancel it");

			foreach (string other in others)
				builder.Text($", or {other}");

			return;
		}

		builder.Text(AreaSentences.JoinClauses(others));
	}

	private static string Name(HouseModeOptionConfig option) =>
		option.Value is { Length: > 0 } value ? value : "This option";

	private static string NameOf(IReadOnlyList<TimePeriodConfig> periods, string periodId) =>
		periods.ByKey(periodId)?.Name is { Length: > 0 } name
			? name
			: periodId.Trim();
}
