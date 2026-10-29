using System.Text.Json;

using Adaptive.Settings;
using Adaptive.UI;

using AdaptiveHeating.Page.Settings;
using AdaptiveHeating.Planner;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>A Home Assistant dropdown that names the period is read as lighting reads it.</summary>
public sealed class ThePeriodDropdownTests
{
	private const string Dropdown = "input_select.period";

	// Noon on the household's clock: the schedule alone places the morning period.
	private static readonly DateTimeOffset Noon = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

	[Theory]
	[InlineData(PeriodAuthority.HomeAssistant, "Natt", 16)]
	[InlineData(PeriodAuthority.HomeAssistant, "Ukjent", 20)]
	[InlineData(PeriodAuthority.AdaptiveLighting, "Natt", 20)]
	public void The_dropdown_names_the_period_only_under_home_assistant_and_only_with_a_mapped_option(
		PeriodAuthority authority,
		string showing,
		double home)
	{
		HeatingConfig document = HeatingConfig.CreateDefault();
		document.Periods =
		[
			new TimePeriodConfig { Id = "period_001", Name = "Morning", Start = "06:00", Temperature = 20 },
			new TimePeriodConfig { Id = "period_002", Name = "Night", Start = "23:00", Temperature = 16 }
		];
		document.Areas = [new AreaConfig { AreaId = ACabin.RoomId }];
		document.Global.PeriodSelect = new PeriodSelectConfig
		{
			Entity = Dropdown,
			Authority = authority,
			Options = [new PeriodSelectOptionConfig { Value = "Natt", PeriodId = "period_002" }]
		};

		AHouseUnderTest house = new(JsonSerializer.Deserialize<EntityState>(
			$$"""{ "entity_id": "{{Dropdown}}", "state": "{{showing}}", "attributes": { "options": ["Natt", "Ukjent"] } }""")!);

		HaCatalog catalog = new(() => house, () => house, NullLoggerFactory.Instance);

		HeatingDocument composed = RoomTemperatures
			.FromTheDocument(() => document, catalog, () => null, TimeZoneInfo.Utc, new ClockUnderTest(Noon))
			.Invoke(ACabin.WithOneRoom());

		Assert.Equal(home, composed.Rooms[0].ModeTemperatures[HeatingMode.Home].Value);
	}
}
