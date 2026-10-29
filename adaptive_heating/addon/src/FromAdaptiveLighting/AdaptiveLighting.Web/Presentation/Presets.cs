namespace AdaptiveLighting.Web.Presentation;

/// <summary>The preset lists the period editor offers, shared so every temperature slider agrees.</summary>
/// <remarks>Choices, not limits: a stored value off the list is shown as its own entry, never snapped to a neighbour.</remarks>
public static class Presets
{
	// The thermostat's own bounds and step: ValidationRanges and the page's NumberField hold the same three.
	private const double Lowest = 2;
	private const double Highest = 30;
	private const double Step = 0.5;

	public static readonly IReadOnlyList<PresetChoice> TemperatureCelsius =
	[
		.. Enumerable.Range(0, (int)((Highest - Lowest) / Step) + 1)
			.Select(index => Lowest + (index * Step))
			.Select(celsius => new PresetChoice(celsius, $"{celsius.ToString("0.#", CultureInfo.InvariantCulture)} °C"))
	];
}
