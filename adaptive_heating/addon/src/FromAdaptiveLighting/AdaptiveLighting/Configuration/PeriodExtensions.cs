using Adaptive.Engine;

namespace Adaptive.Settings;

/// <summary>Resolving a period out of the schedule.</summary>
/// <remarks>Cannot live in <c>AdaptiveLighting.Extensions</c>: that package is host-agnostic and knows no configuration type.</remarks>
public static class PeriodExtensions
{
	/// <summary>The period answering to <paramref name="key"/>, or <c>null</c> when none does.</summary>
	/// <remarks>
	///     <see cref="TimePeriodConfig.Key"/> is the id once a document has one and the display name until then. This
	///     is the match the engine resolves by, so a page asking it can never badge a period the engine leaves unresolved.
	/// </remarks>
	public static TimePeriodConfig? ByKey(this IEnumerable<TimePeriodConfig> periods, string? key)
	{
		ArgumentNullException.ThrowIfNull(periods);

		return periods.FirstOrDefault(period => period.Key.SameName(key));
	}

	/// <summary>The period whose display name matches, or <c>null</c> when none does.</summary>
	/// <remarks>A guess at what was meant, not a reference. Everything else resolves through <see cref="ByKey"/>.</remarks>
	public static TimePeriodConfig? ByName(this IEnumerable<TimePeriodConfig> periods, string? name)
	{
		ArgumentNullException.ThrowIfNull(periods);

		return periods.FirstOrDefault(period => period.Name.SameName(name));
	}
}
