using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>A theme id is a storage key in the browser: changing one silently resets everybody who chose it.</summary>
public sealed class TheStoredThemeIdsTests
{
	[Fact]
	public void The_ids_and_the_two_defaults_are_lightings()
	{
		Assert.Equal(
			["system", "warm-charcoal", "plum-dusk", "blackout", "0z0", "paper", "fern"],
			HeatingThemes.All.Select(theme => theme.Id).ToArray());

		Assert.Same(HeatingThemes.WarmCharcoal, HeatingThemes.DarkDefault);
		Assert.Same(HeatingThemes.Paper, HeatingThemes.LightDefault);
	}
}
