using System.Reactive.Concurrency;
using System.Text.Json;

using Adaptive.Engine;
using Adaptive.Settings;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>What the start of a period does to the house mode and to the dropdown naming the period.</summary>
public sealed class ThePeriodTrackerTests
{
	private const string HouseMode = "input_select.house_mode";
	private const string Dropdown = "input_select.period";

	private static readonly DateTimeOffset Day = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);

	[Theory]
	[InlineData("22:58", "Hjemme", "Sover")]
	[InlineData("05:58", "Borte", "Hjemme")]
	public void A_period_start_sets_the_house_mode_its_period_or_the_mode_asks_for(
		string startsAt,
		string showing,
		string expected)
	{
		AHouseUnderTest house = new(
			CoreUnderTest.State(HouseMode, showing, """{ "options": ["Hjemme", "Sover", "Borte"] }"""));

		RunAcrossTheBoundary(house, PeriodAuthority.AdaptiveLighting, startsAt);

		Assert.Equal([expected], OptionsWrittenTo(house, HouseMode));
	}

	// An option no period maps leaves the clock deciding, the one case where a write would overrule the household.
	[Theory]
	[InlineData(PeriodAuthority.AdaptiveLighting, new[] { "Morgen", "Natt" })]
	[InlineData(PeriodAuthority.HomeAssistant, new string[0])]
	public void Only_the_owner_of_the_dropdown_writes_the_period_into_it(PeriodAuthority authority, string[] expected)
	{
		AHouseUnderTest house = new(
			CoreUnderTest.State(HouseMode, "Hjemme", """{ "options": ["Hjemme", "Sover", "Borte"] }"""),
			CoreUnderTest.State(Dropdown, "Ukjent", """{ "options": ["Morgen", "Natt", "Ukjent"] }"""));

		LogUnderTest<ModeMonitor> log = new();
		RunAcrossTheBoundary(house, authority, "22:58", log);

		Assert.Equal(expected, OptionsWrittenTo(house, Dropdown));
		Assert.Equal(
			authority is PeriodAuthority.HomeAssistant ? 1 : 0,
			log.Lines.Count(line => line.Level == LogLevel.Warning && line.Said.Contains("Ukjent", StringComparison.Ordinal)));
	}

	private static void RunAcrossTheBoundary(
		AHouseUnderTest house,
		PeriodAuthority authority,
		string startsAt,
		ILogger? log = null)
	{
		TimeSpan start = TimeSpan.Parse(startsAt, System.Globalization.CultureInfo.InvariantCulture);
		HistoricalScheduler scheduler = new(Day + start);

		GlobalConfig global = new()
		{
			HouseMode = new HouseModeConfig
			{
				Entity = HouseMode,
				Options =
				[
					new HouseModeOptionConfig { Id = "Hjemme", Value = "Hjemme", Kind = ModeKind.Normal },
					new HouseModeOptionConfig { Id = "Sover", Value = "Sover", Kind = ModeKind.Sleep },
					new HouseModeOptionConfig { Id = "Borte", Value = "Borte", Kind = ModeKind.Away, ResetOnPeriodStartId = "period_001" }
				]
			},
			PeriodSelect = new PeriodSelectConfig
			{
				Entity = Dropdown,
				Authority = authority,
				Options =
				[
					new PeriodSelectOptionConfig { Value = "Morgen", PeriodId = "period_001" },
					new PeriodSelectOptionConfig { Value = "Natt", PeriodId = "period_002" }
				]
			}
		};

		List<TimePeriodConfig> periods =
		[
			new TimePeriodConfig { Id = "period_001", Name = "Morning", Start = "06:00" },
			new TimePeriodConfig { Id = "period_002", Name = "Night", Start = "23:00", SetsModeId = "Sover" }
		];

		using ModeMonitor monitor = new(
			house,
			global,
			NullLogger.Instance,
			scheduler,
			periods,
			() => new SunTimes(null, null),
			areaMotionSensors: [],
			periodSelect: PeriodSelectReader.For(house, global, log ?? NullLogger.Instance),
			zone: TimeZoneInfo.Utc);

		monitor.Start();
		scheduler.AdvanceTo(Day + start + TimeSpan.FromMinutes(5));
	}

	private static string[] OptionsWrittenTo(AHouseUnderTest house, string entityId) =>
	[
		.. house.Calls
			.Where(call => call.Service == "select_option" && call.Target?.EntityIds?.Contains(entityId) == true)
			.Select(call => JsonSerializer.SerializeToElement(call.Data).GetProperty("option").GetString()!)
			.Distinct()
	];
}
