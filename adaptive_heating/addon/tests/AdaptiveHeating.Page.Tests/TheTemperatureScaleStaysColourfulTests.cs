using AdaptiveHeating.Page.Presentation;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>Every temperature on the scale keeps a colour; a scale that greys out in the middle tells nobody anything.</summary>
public sealed class TheTemperatureScaleStaysColourfulTests
{
	private const double LeastChroma = 0.07;

	[Fact]
	public void No_half_degree_from_the_coldest_to_the_warmest_falls_to_grey()
	{
		for (double celsius = TemperatureColour.Coldest; celsius <= TemperatureColour.Warmest; celsius += 0.5)
		{
			(int red, int green, int blue) = TemperatureColour.Rgb(celsius);
			double chroma = TemperatureColour.ChromaOf(red, green, blue);

			Assert.True(chroma >= LeastChroma, $"{celsius} °C is rgb({red}, {green}, {blue}), chroma {chroma:0.000}.");
		}
	}
}
