using Adaptive.Settings;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>The dropdown that is obviously the house mode, and what each of its options is read as.</summary>
public sealed class TheHouseModeClassifierTests
{
	private static AHouseUnderTest WithSelect(string entityId, params string[] options) =>
		new(Select(entityId, options));

	private static EntityState Select(string entityId, params string[] options) =>
		CoreUnderTest.State(
			entityId,
			options.FirstOrDefault() ?? "",
			$$"""{ "options": [{{string.Join(", ", options.Select(option => $"\"{option}\""))}}] }""");

	[Fact]
	public void The_Obvious_House_Mode_Dropdown_Is_Adopted_And_Classified()
	{
		AHouseUnderTest ha = WithSelect("input_select.house_state", "Home", "Away", "Sleeping", "Guests");

		HouseModeConfig? detected = HouseModeAutoDetect.Detect(ha, NullLogger.Instance);

		Assert.NotNull(detected);
		Assert.Equal("input_select.house_state", detected.Entity);
		Assert.Equal(ModeKind.Normal, detected.OptionFor("Home")!.Kind);
		Assert.Equal(ModeKind.Away, detected.OptionFor("Away")!.Kind);
		Assert.Equal(ModeKind.Sleep, detected.OptionFor("Sleeping")!.Kind);

		// Read-only on adoption: nothing here can make the engine write the select.
		Assert.True(detected.Options.All(option => !option.HasResetTrigger), "no reset trigger");
	}

	[Fact]
	public void Norwegian_Options_Are_Understood()
	{
		HouseModeConfig? detected = HouseModeAutoDetect.Detect(
			WithSelect("input_select.husmodus", "Hjemme", "Borte", "Sover", "Gjester"), NullLogger.Instance);

		Assert.NotNull(detected);
		Assert.Equal(ModeKind.Away, detected.OptionFor("Borte")!.Kind);
		Assert.Equal(ModeKind.Sleep, detected.OptionFor("Sover")!.Kind);
	}

	// A dropdown that is not about the house classifies as all-Normal, and all-Normal is never adopted.
	[Fact]
	public void An_Unrelated_Dropdown_Is_Not_Adopted()
	{
		Assert.Null(HouseModeAutoDetect.Detect(
			WithSelect("input_select.thermostat_profile", "Eco", "Comfort", "Boost"), NullLogger.Instance));
	}

	[Fact]
	public void An_Adopted_Select_Always_Carries_A_Kind_That_Changes_What_The_House_Does()
	{
		HouseModeConfig? detected = HouseModeAutoDetect.Detect(
			WithSelect("input_select.torkeskap", "Inne", "Ute"), NullLogger.Instance);

		Assert.NotNull(detected);
		Assert.Equal(ModeKind.Away, detected.OptionFor("Ute")!.Kind);
		Assert.True(
			detected.Options.Any(option => option.Kind != ModeKind.Normal),
			"an all-Normal select cannot be adopted, so adoption can never be inert");
		Assert.True(
			detected.Options.All(option => !option.HasResetTrigger),
			"nothing adopted here can make the engine write the select");
	}

	[Fact]
	public void Two_Candidates_Means_Choosing_Neither()
	{
		AHouseUnderTest ha = WithSelect("input_select.house_state", "Home", "Away", "Sleeping");
		ha.Holds(Select("input_select.hytta", "Hjemme", "Borte"));

		Assert.Null(HouseModeAutoDetect.Detect(ha, NullLogger.Instance));
	}

	[Fact]
	public void The_cabins_options_read_as_normal_away_and_sleep_and_an_unknown_word_as_normal()
	{
		HouseModeConfig? detected = HouseModeAutoDetect.Detect(
			WithSelect("input_select.hytta", "Normal", "Borte", "Sover", "Vedlikehold"), NullLogger.Instance);

		Assert.NotNull(detected);
		Assert.Equal(ModeKind.Normal, detected.OptionFor("Normal")!.Kind);
		Assert.Equal(ModeKind.Away, detected.OptionFor("Borte")!.Kind);
		Assert.Equal(ModeKind.Sleep, detected.OptionFor("Sover")!.Kind);
		Assert.Equal(ModeKind.Normal, detected.OptionFor("Vedlikehold")!.Kind);
	}
}
