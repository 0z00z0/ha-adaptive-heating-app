using System.Globalization;
using System.Text.Json;

using NetDaemon.HassModel.Entities;

namespace Adaptive.Engine;

/// <summary>Reads values out of an <see cref="EntityState"/> and its attribute bag, tolerantly.</summary>
/// <remarks>
///     An attribute may arrive as a <see cref="JsonElement"/>, a boxed primitive or a string, so every read here
///     handles all three. Nothing throws: an absent state and a wrongly shaped attribute read as null, false or empty.
/// </remarks>
public static class EntityStateExtensions
{
	/// <summary>Reads a numeric attribute, or <c>null</c> when the state or attribute is absent or not a number.</summary>
	public static double? AttrDouble(this EntityState? state, string attribute)
	{
		if (!TryGetValue(state, attribute, out object? value))
			return null;

		return value switch
		{
			JsonElement { ValueKind: JsonValueKind.Number } element => element.GetDouble(),
			JsonElement { ValueKind: JsonValueKind.String } element when TryParse(element.GetString(), out double parsed) => parsed,
			double number => number,
			float number => number,
			long number => number,
			int number => number,
			string text when TryParse(text, out double parsed) => parsed,
			_ => null
		};

		static bool TryParse(string? text, out double parsed) =>
			double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
	}

	/// <summary>Reads a string attribute, or <c>null</c> when the state or attribute is absent.</summary>
	public static string? AttrString(this EntityState? state, string attribute)
	{
		if (!TryGetValue(state, attribute, out object? value))
			return null;

		return value switch
		{
			JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
			string text => text,
			_ => null
		};
	}

	/// <summary>Reads an attribute holding a list of numbers (a colour channel array), empty when absent or not a list.</summary>
	public static IReadOnlyList<double> AttrDoubleList(this EntityState? state, string attribute)
	{
		if (!TryGetValue(state, attribute, out object? value))
			return [];

		return value switch
		{
			JsonElement { ValueKind: JsonValueKind.Array } element =>
				[.. element.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Number).Select(item => item.GetDouble())],
			IEnumerable<int> numbers => [.. numbers.Select(number => (double)number)],
			IEnumerable<double> numbers => [.. numbers],
			_ => []
		};
	}

	/// <summary>Reads an attribute holding a list of strings, empty when absent or not a list.</summary>
	public static IReadOnlyList<string> AttrStringList(this EntityState? state, string attribute)
	{
		if (!TryGetValue(state, attribute, out object? value))
			return [];

		switch (value)
		{
			case JsonElement { ValueKind: JsonValueKind.Array } element:
				return [.. element.EnumerateArray()
					.Where(item => item.ValueKind == JsonValueKind.String)
					.Select(item => item.GetString()!)];

			case JsonElement { ValueKind: JsonValueKind.String } element:
				return element.GetString() is { } single ? [single] : [];

			case string text:
				return [text];

			case IEnumerable<string> strings:
				return [.. strings];

			case System.Collections.IEnumerable items:
				return [.. items.OfType<object>()
					.Select(item => Convert.ToString(item, CultureInfo.InvariantCulture) ?? "")
					.Where(item => item.Length > 0)];

			default:
				return [];
		}
	}

	/// <summary>Reads an attribute holding an ISO-8601 timestamp, or <c>null</c> when absent or unparseable.</summary>
	/// <remarks>
	///     Invariant culture, AssumeUniversal and AdjustToUniversal, matching how Home Assistant publishes sun.sun's
	///     next_rising and next_setting. The result is UTC; call ToLocalTime for wall-clock terms.
	/// </remarks>
	public static DateTimeOffset? AttrDateTimeOffset(this EntityState? state, string attribute) =>
		state.AttrString(attribute) is { } text
		&& DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed)
			? parsed
			: null;

	/// <summary>The state parsed as a number (invariant culture), or <c>null</c> when absent or not a number.</summary>
	public static double? StateAsDouble(this EntityState? state) =>
		state?.State is { } text
		&& double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
			? value
			: null;

	/// <summary>Whether the entity has a state and that state is not <c>unavailable</c>.</summary>
	/// <remarks>
	///     Lets <c>unknown</c> through, where <see cref="AsUsableState"/> and the engine's
	///     <c>AreaEntityResolver.IsLive</c> both drop it. Three predicates, three answers; do not unify them.
	/// </remarks>
	public static bool IsAvailable(this EntityState? state) =>
		state is not null && !string.Equals(state.State, "unavailable", StringComparison.OrdinalIgnoreCase);

	/// <summary>Whether the state equals <paramref name="value"/>, ordinal-ignore-case.</summary>
	public static bool StateIs(this EntityState? state, string value) =>
		string.Equals(state?.State, value, StringComparison.OrdinalIgnoreCase);

	/// <summary>The trimmed state string, or <c>null</c> when the state is absent, <c>unknown</c> or <c>unavailable</c>.</summary>
	/// <remarks>
	///     A select sitting on <c>unknown</c> must classify as no mode, so this drops <c>unknown</c> where
	///     <see cref="IsAvailable"/> keeps it.
	/// </remarks>
	public static string? AsUsableState(this EntityState? state)
	{
		string? raw = state?.State;
		return raw is null or "unknown" or "unavailable" ? null : raw.Trim();
	}

	private static bool TryGetValue(EntityState? state, string attribute, out object? value)
	{
		value = null;
		return state?.Attributes?.TryGetValue(attribute, out value) == true && value is not null;
	}
}
