using System.Reflection;

using Adaptive.Engine;
using Adaptive.Settings;
using Adaptive.UI;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>The settings document and the one write path over it.</summary>
public sealed class OneDocumentOneWritePathTests
{
	private const string Forecast = AHostedDocument.Forecast;

	private const string ArrivalCalendar = "calendar.arrivals";

	[Fact]
	public void A_save_that_clears_the_forecast_entity_leaves_the_document_with_none()
	{
		AHostedDocument document = AHostedDocument.Runnable();

		// The control: without this the clearing save has nothing to clear and the test cannot fail.
		Assert.Contains($"ForecastEntity: {Forecast}", File.ReadAllText(document.Host.Store.FilePath), StringComparison.Ordinal);

		HeatingConfig clearing = document.Host.Store.Load();
		clearing.Global.ForecastEntity = null;

		Assert.True(document.Host.Save(clearing).Written);

		string written = File.ReadAllText(document.Host.Store.FilePath);

		Assert.DoesNotContain("ForecastEntity", written, StringComparison.Ordinal);
		Assert.DoesNotContain(Forecast, written, StringComparison.Ordinal);
		Assert.Null(document.Host.Store.Load().Global.ForecastEntity);
	}

	[Fact]
	public void A_save_that_clears_the_arrival_calendar_leaves_the_document_with_none()
	{
		HeatingConfig naming = HeatingConfig.CreateDefault();
		naming.Global.ArrivalCalendar = ArrivalCalendar;

		AHostedDocument document = AHostedDocument.Holding(HeatingConfigDocument.Serialize(naming));

		Assert.Contains($"ArrivalCalendar: {ArrivalCalendar}", File.ReadAllText(document.Host.Store.FilePath), StringComparison.Ordinal);

		HeatingConfig clearing = document.Host.Store.Load();
		clearing.Global.ArrivalCalendar = null;

		Assert.True(document.Host.Save(clearing).Written);

		string written = File.ReadAllText(document.Host.Store.FilePath);

		Assert.DoesNotContain("ArrivalCalendar", written, StringComparison.Ordinal);
		Assert.DoesNotContain(ArrivalCalendar, written, StringComparison.Ordinal);
		Assert.Null(document.Host.Store.Load().Global.ArrivalCalendar);
	}

	[Fact]
	public void A_save_with_a_stale_stamp_is_refused_with_the_hosts_own_message()
	{
		AHostedDocument document = AHostedDocument.Runnable();

		HeatingConfig asThePageReadIt = document.Host.Store.Load();
		string stamp = ConfigStamp.OfDocument(asThePageReadIt);

		// Somebody else writes in between, which is what moves the file on from what the page was shown.
		HeatingConfig other = document.Host.Store.Load();
		other.Global.ForecastEntity = "weather.elsewhere";
		Assert.True(document.Host.Save(other).Written);

		asThePageReadIt.Global.OutdoorSensor = "sensor.outdoor";

		SaveResult refused = DocumentWrite.Save(document.Host, stamp, asThePageReadIt);

		Assert.Equal(SaveStatus.Conflicted, refused.Status);
		Assert.False(refused.Written);
		Assert.Contains("changed somewhere else", refused.Message, StringComparison.Ordinal);

		// The other writer's value is still there, which is the whole point of refusing rather than writing.
		Assert.Equal("weather.elsewhere", document.Host.Store.Load().Global.ForecastEntity);
	}

	[Fact]
	public void A_key_this_build_does_not_read_loads_and_survives_a_save()
	{
		AHostedDocument document = AHostedDocument.Holding(
			$"""
			{HeatingConfigDocument.RootKey}:
			  ConfigName: Adaptive Heating
			  SomethingThisBuildNeverHeardOf: keep me
			  Global:
			    BlendMinutes: 30
			    ForecastEntity: weather.cabin
			    AlsoUnheardOf: 42
			  Areas:
			  - AreaId: kitchen
			""");

		HeatingConfig loaded = document.Host.Store.Load();

		Assert.Equal("weather.cabin", loaded.Global.ForecastEntity);

		loaded.Global.OutdoorSensor = "sensor.outdoor";
		Assert.True(document.Host.Save(loaded).Written);

		string written = File.ReadAllText(document.Host.Store.FilePath);

		Assert.Contains("SomethingThisBuildNeverHeardOf: keep me", written, StringComparison.Ordinal);
		Assert.Contains("AlsoUnheardOf: 42", written, StringComparison.Ordinal);
		Assert.Contains("OutdoorSensor: sensor.outdoor", written, StringComparison.Ordinal);

		// A retired key is the one kind a save drops.
		Assert.DoesNotContain("BlendMinutes", written, StringComparison.Ordinal);
	}

	[Fact]
	public void A_document_naming_the_forecast_with_no_forecast_entity_is_saved_with_a_warning()
	{
		AHostedDocument document = AHostedDocument.Holding(HeatingConfigDocument.Serialize(HeatingConfig.CreateDefault()));

		HeatingConfig naming = document.Host.Store.Load();
		naming.Global.WarmUpSource = WarmUpSource.Forecast;
		naming.Global.ForecastEntity = null;

		SaveResult saved = document.Host.Save(naming);

		Assert.True(saved.Written);
		Assert.Empty(saved.Validation.Errors);
		Assert.Contains(saved.Validation.Warnings, warning => warning.Contains("forecast has no entity", StringComparison.Ordinal));

		// The control: the same document with a forecast entity named carries no such warning.
		naming.Global.ForecastEntity = Forecast;

		SaveResult named = document.Host.Save(naming);

		Assert.True(named.Written);
		Assert.DoesNotContain(named.Validation.Warnings, warning => warning.Contains("forecast has no entity", StringComparison.Ordinal));
	}

	[Fact]
	public void A_document_created_from_the_defaults_is_valid()
	{
		AHostedDocument document = AHostedDocument.Holding(HeatingConfigDocument.Serialize(HeatingConfig.CreateDefault()));

		SaveResult saved = document.Host.Save(document.Host.Store.Load());

		Assert.True(saved.Validation.IsValid);
		Assert.True(saved.Written);
	}

	[Theory]
	[InlineData("morning", "Late morning", "Two periods share the id 'morning'")]
	[InlineData(null, "", "A period has no Name")]
	public void A_period_key_the_document_cannot_resolve_is_refused_with_the_hosts_own_message(string? secondId, string secondName, string error)
	{
		AHostedDocument document = AHostedDocument.Runnable();
		string before = File.ReadAllText(document.Host.Store.FilePath);

		HeatingConfig refusing = document.Host.Store.Load();
		refusing.Periods =
		[
			new TimePeriodConfig { Id = "morning", Name = "Morning", Start = "06:30", Temperature = 21 },
			new TimePeriodConfig { Id = secondId, Name = secondName, Start = "08:00" },
		];

		SaveResult refused = document.Host.Save(refusing);

		Assert.Equal(SaveStatus.Rejected, refused.Status);
		Assert.Equal("Not saved. The file on disk is untouched.", refused.Message);
		Assert.Contains(refused.Validation.Errors, said => said.Contains(error, StringComparison.Ordinal));
		Assert.Equal(before, File.ReadAllText(document.Host.Store.FilePath));
	}

	[Fact]
	public void A_period_that_sets_the_house_mode_keeps_its_mode_and_temperature_across_a_restart()
	{
		AHostedDocument document = AHostedDocument.Runnable();

		Assert.DoesNotContain("SetsModeId", File.ReadAllText(document.Host.Store.FilePath), StringComparison.Ordinal);

		HeatingConfig naming = document.Host.Store.Load();
		naming.Global.HouseMode = new HouseModeConfig { Options = [new HouseModeOptionConfig { Id = "away", Value = "Borte", Kind = ModeKind.Away }] };
		naming.Periods = [new TimePeriodConfig { Id = "evening", Name = "Evening", Start = "sunset-01:00", SetsModeId = "away", Temperature = 19.5 }];

		Assert.True(document.Host.Save(naming).Written);

		HeatingDocumentHost restarted = new(
			new HeatingConfigStore(document.Host.Store.FilePath, NullLogger<HeatingConfigStore>.Instance),
			_ => ValidationContext.None,
			() => { },
			NullLogger<HeatingDocumentHost>.Instance);

		Assert.Equal(SaveStatus.Saved, restarted.Reload().Status);

		TimePeriodConfig period = Assert.Single(restarted.Current.Periods);
		Assert.Equal("away", period.SetsModeId);
		Assert.Equal(19.5, period.Temperature);
		Assert.Equal("evening", period.Key);
	}

	[Fact]
	public void A_house_mode_choice_reaches_the_file_and_survives_a_restart()
	{
		AHostedDocument document = AHostedDocument.Runnable();

		Assert.DoesNotContain("HouseMode", File.ReadAllText(document.Host.Store.FilePath), StringComparison.Ordinal);

		HeatingConfig choosing = document.Host.Store.Load();
		choosing.Global.HouseMode = new HouseModeConfig
		{
			Entity = "input_select.hytta",
			Options =
			[
				new HouseModeOptionConfig { Value = "Normal", Kind = ModeKind.Normal },
				new HouseModeOptionConfig { Value = "Borte", Kind = ModeKind.Away },
			],
		};

		Assert.True(document.Host.Save(choosing).Written);
		Assert.Contains("Entity: input_select.hytta", File.ReadAllText(document.Host.Store.FilePath), StringComparison.Ordinal);

		HouseModeConfig? houseMode = Restarted(document).Global.HouseMode;

		Assert.Equal("input_select.hytta", houseMode?.EntityId);
		Assert.Equal(ModeKind.Away, houseMode?.OptionFor("Borte")?.Kind);
	}

	[Fact]
	public void A_choice_on_the_house_modes_tab_reaches_the_file_and_survives_a_restart()
	{
		AHostedDocument document = AHostedDocument.Runnable();

		Assert.DoesNotContain("HouseMode", File.ReadAllText(document.Host.Store.FilePath), StringComparison.Ordinal);

		AHouseUnderTest house = new(CoreUnderTest.State("input_select.house_mode", "Home", """{ "options": ["Home", "Away"] }"""));
		HaCatalog catalog = new(() => house, () => house, NullLoggerFactory.Instance);
		using HousePageModel tab = new(
			document.Host,
			catalog,
			new ModeService(() => house, catalog, document.Host, NullLogger<ModeService>.Instance),
			NullLogger<HousePageModel>.Instance,
			new AdaptiveHeating.Page.Settings.InMemorySettingsStore(ACabin.WithOneRoom()));

		tab.Start();
		tab.SetHouseModeEntity("input_select.house_mode");
		tab.SetHouseModeAuthority(homeAssistant: true);
		tab.HouseMode!.Options.Add(new HouseModeOptionConfig { Value = "Away", Kind = ModeKind.Away });
		tab.OnHouseModeOptionsChanged();
		tab.Save();

		Assert.False(tab.HasUnsavedEdits);

		HouseModeConfig? houseMode = Restarted(document).Global.HouseMode;

		Assert.Equal("input_select.house_mode", houseMode?.EntityId);
		Assert.Equal(HouseModeAuthority.HomeAssistant, houseMode?.Authority);
		Assert.Equal(ModeKind.Away, houseMode?.OptionFor("Away")?.Kind);
	}

	[Fact]
	public void A_period_select_choice_reaches_the_file_and_survives_a_restart()
	{
		AHostedDocument document = AHostedDocument.Runnable();

		Assert.DoesNotContain("PeriodSelect", File.ReadAllText(document.Host.Store.FilePath), StringComparison.Ordinal);

		HeatingConfig choosing = document.Host.Store.Load();
		choosing.Periods = [new TimePeriodConfig { Id = "evening", Name = "Evening", Start = "18:00", Temperature = 20 }];
		choosing.Global.PeriodSelect = new PeriodSelectConfig
		{
			Entity = "input_select.tid",
			Options = [new PeriodSelectOptionConfig { Value = "Kveld", PeriodId = "evening" }],
		};

		Assert.True(document.Host.Save(choosing).Written);
		Assert.Contains("Entity: input_select.tid", File.ReadAllText(document.Host.Store.FilePath), StringComparison.Ordinal);

		PeriodSelectConfig? periodSelect = Restarted(document).Global.PeriodSelect;

		Assert.Equal("input_select.tid", periodSelect?.EntityId);
		Assert.Equal("evening", periodSelect?.PeriodFor("Kveld"));
	}

	[Fact]
	public void A_house_mode_naming_an_entity_the_house_has_not_got_is_refused_with_the_hosts_own_message()
	{
		HeatingConfig runnable = HeatingConfig.CreateDefault();
		runnable.Global.ForecastEntity = Forecast;

		AHostedDocument document = AHostedDocument.Holding(
			HeatingConfigDocument.Serialize(runnable),
			_ => new ValidationContext { KnownEntityIds = [Forecast] });
		string before = File.ReadAllText(document.Host.Store.FilePath);

		HeatingConfig refusing = document.Host.Store.Load();
		refusing.Global.HouseMode = new HouseModeConfig { Entity = "input_select.missing" };

		SaveResult refused = document.Host.Save(refusing);

		Assert.Equal(SaveStatus.Rejected, refused.Status);
		Assert.Equal("Not saved. The file on disk is untouched.", refused.Message);
		Assert.Contains("Global.HouseMode.Entity refers to 'input_select.missing', which Home Assistant does not know.", refused.Validation.Errors);
		Assert.Equal(before, File.ReadAllText(document.Host.Store.FilePath));
	}

	[Fact]
	public void A_document_left_with_both_sections_empty_is_valid_and_writes_neither()
	{
		AHostedDocument document = AHostedDocument.Runnable();

		// A save with neither section is the baseline: its bytes are what a document that never named either holds.
		Assert.True(document.Host.Save(document.Host.Store.Load()).Written);
		string before = File.ReadAllText(document.Host.Store.FilePath);

		HeatingConfig emptied = document.Host.Store.Load();
		emptied.Global.HouseMode = new HouseModeConfig();
		emptied.Global.PeriodSelect = new PeriodSelectConfig();

		SaveResult saved = document.Host.Save(emptied);

		Assert.True(saved.Written);
		Assert.True(saved.Validation.IsValid);
		Assert.Equal(before, File.ReadAllText(document.Host.Store.FilePath));
	}

	private static HeatingConfig Restarted(AHostedDocument document)
	{
		HeatingDocumentHost restarted = new(
			new HeatingConfigStore(document.Host.Store.FilePath, NullLogger<HeatingConfigStore>.Instance),
			_ => ValidationContext.None,
			() => { },
			NullLogger<HeatingDocumentHost>.Instance);

		Assert.Equal(SaveStatus.Saved, restarted.Reload().Status);

		return restarted.Current;
	}

	/// <summary>A cabin back from a power cut keeps whatever time its hardware held, which can read hours behind the
	/// settings file. Discarding the file then empties every temperature a person typed.</summary>
	[Fact]
	public void A_document_saved_an_hour_ahead_of_the_clock_loads_intact()
	{
		AHostedDocument document = AHostedDocument.Holding(
			$"""
			{HeatingConfigDocument.RootKey}:
			  ConfigName: Adaptive Heating
			  SavedAt: 2099-01-01T00:00:00Z
			  Global:
			    ForecastEntity: weather.cabin
			    PassCadenceSeconds: 45
			""");

		HeatingConfig loaded = document.Host.Store.Load();

		Assert.Equal("weather.cabin", loaded.Global.ForecastEntity);

		// The other direction: 60 is what a fresh document holds, so reading 60 would be a discarded file.
		Assert.Equal(45, loaded.Global.PassCadenceSeconds);
		Assert.NotEqual(60, loaded.Global.PassCadenceSeconds);

		// By construction, not by inspection: nothing on the way to or from the file is handed a clock, so no
		// comparison against one can be reintroduced without this failing.
		Assert.Empty(ClockParametersOf(typeof(HeatingConfigStore)));
		Assert.Empty(ClockParametersOf(typeof(HeatingConfigDocument)));
	}

	private static IReadOnlyList<string> ClockParametersOf(Type type) =>
		[.. type
			.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
			.OfType<MethodBase>()
			.SelectMany(member => member.GetParameters())
			.Where(parameter => parameter.ParameterType == typeof(TimeProvider)
				|| parameter.ParameterType == typeof(DateTimeOffset)
				|| parameter.ParameterType == typeof(DateTime))
			.Select(parameter => $"{parameter.Member.DeclaringType?.Name}.{parameter.Member.Name}({parameter.Name})")];

}
