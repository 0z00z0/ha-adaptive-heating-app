namespace AdaptiveHeating.Page.Presentation;

/// <summary>A room temperature as a CSS colour: muted blue at <see cref="Coldest"/>, muted red at <see cref="Warmest"/>.</summary>
public static class TemperatureColour
{
	public const double Coldest = 14;

	public const double Warmest = 26;

	// OKLCH. Lightness and chroma stay fixed and only the hue turns, through teal, sage and amber: a straight blend
	// between the two ends passes through grey.
	public const double Lightness = 0.64;

	public const double Chroma = 0.10;

	public const double ColdHue = 245;

	public const double WarmHue = 30;

	public static string Css(double celsius)
	{
		(int red, int green, int blue) = Rgb(celsius);

		return string.Create(CultureInfo.InvariantCulture, $"rgb({red}, {green}, {blue})");
	}

	public static (int Red, int Green, int Blue) Rgb(double celsius)
	{
		double along = (Math.Clamp(celsius, Coldest, Warmest) - Coldest) / (Warmest - Coldest);
		double hue = (ColdHue + ((WarmHue - ColdHue) * along)) * Math.PI / 180;

		return FromOklab(Lightness, Chroma * Math.Cos(hue), Chroma * Math.Sin(hue));
	}

	/// <summary>How colourful an sRGB colour is: its OKLab chroma.</summary>
	public static double ChromaOf(int red, int green, int blue)
	{
		double r = Linear(red);
		double g = Linear(green);
		double b = Linear(blue);

		double l = Math.Cbrt((0.4122214708 * r) + (0.5363325363 * g) + (0.0514459929 * b));
		double m = Math.Cbrt((0.2119034982 * r) + (0.6806995451 * g) + (0.1073969566 * b));
		double s = Math.Cbrt((0.0883024619 * r) + (0.2817188376 * g) + (0.6299787005 * b));

		double a = (1.9779984951 * l) - (2.4285922050 * m) + (0.4505937099 * s);
		double bb = (0.0259040371 * l) + (0.7827717662 * m) - (0.8086757660 * s);

		return Math.Sqrt((a * a) + (bb * bb));
	}

	private static (int Red, int Green, int Blue) FromOklab(double lightness, double a, double b)
	{
		double l = Math.Pow(lightness + (0.3963377774 * a) + (0.2158037573 * b), 3);
		double m = Math.Pow(lightness - (0.1055613458 * a) - (0.0638541728 * b), 3);
		double s = Math.Pow(lightness - (0.0894841775 * a) - (1.2914855480 * b), 3);

		return (
			Gamma((4.0767416621 * l) - (3.3077115913 * m) + (0.2309699292 * s)),
			Gamma((-1.2684380046 * l) + (2.6097574011 * m) - (0.3413193965 * s)),
			Gamma((-0.0041960863 * l) - (0.7034186147 * m) + (1.7076147010 * s)));
	}

	private static int Gamma(double linear)
	{
		double clamped = Math.Clamp(linear, 0, 1);
		double encoded = clamped <= 0.0031308 ? 12.92 * clamped : (1.055 * Math.Pow(clamped, 1 / 2.4)) - 0.055;

		return (int)Math.Round(encoded * 255, MidpointRounding.AwayFromZero);
	}

	private static double Linear(int channel)
	{
		double encoded = channel / 255.0;

		return encoded <= 0.04045 ? encoded / 12.92 : Math.Pow((encoded + 0.055) / 1.055, 2.4);
	}
}
