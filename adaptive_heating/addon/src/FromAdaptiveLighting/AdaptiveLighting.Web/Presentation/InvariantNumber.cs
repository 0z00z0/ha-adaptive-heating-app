using System.Globalization;

namespace AdaptiveLighting.Web.Presentation;

/// <summary>A number written into markup: SVG coordinates, CSS lengths, numeric attributes.</summary>
public static class InvariantNumber
{
	private static readonly string[] Formats = ["0", "0.#", "0.##", "0.###", "0.####", "0.#####"];

	// Invariant, always: under nb-NO a double renders 62,5, which no browser reads as a number.
	public static string Format(double value, int decimals) =>
		value.ToString(Formats[Math.Clamp(decimals, 0, Formats.Length - 1)], CultureInfo.InvariantCulture);
}
