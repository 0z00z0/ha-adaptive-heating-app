using Adaptive.Settings;

namespace Adaptive.Engine;

/// <summary>What a room is called, decided once for every surface that has to say it.</summary>
// A lookup, never a write: the document stores only an area id, so a rename in Home Assistant arrives on the
// next read.
public static class AreaNaming
{
	// Order is the rule: a Name in the document overrules Home Assistant, then the registry's name, then the
	// area id. A null registryName skips the registry, which is what a caller with no connection has.
	public static string? Resolve(AreaConfig area, Func<string, string?>? registryName)
	{
		ArgumentNullException.ThrowIfNull(area);

		if (Named(area.Name) is { } stated)
			return stated;

		if (Named(area.AreaId) is not { } areaId)
			return null;

		return Named(registryName?.Invoke(areaId)) ?? areaId;
	}

	/// <inheritdoc cref="Resolve(AreaConfig, Func{string, string?})"/>
	public static string? Resolve(AreaConfig area, IAreaRegistry? registry) => Resolve(area, NameSource(registry));

	public static string DisplayName(AreaConfig area, IAreaRegistry? registry) =>
		Resolve(area, registry) ?? area.DisplayName;

	/// <inheritdoc cref="DisplayName(AreaConfig, IAreaRegistry)"/>
	public static string DisplayName(AreaConfig area, Func<string, string?>? registryName) =>
		Resolve(area, registryName) ?? area.DisplayName;

	// NetDaemon's registry throws until its first connection completes, and Kestrel serves pages in that window.
	public static string? OfArea(IAreaRegistry? registry, string? areaId)
	{
		if (registry is null || Named(areaId) is not { } asked)
			return null;

		try
		{
			return Named(registry.NameOf(asked));
		}
		catch (InvalidOperationException)
		{
			return null;
		}
	}

	// Whitespace is not a name: a hand-edited Name of "  " must fall through to the id.
	private static string? Named(string? candidate) => string.IsNullOrWhiteSpace(candidate) ? null : candidate;

	private static Func<string, string?>? NameSource(IAreaRegistry? registry) =>
		registry is null ? null : areaId => OfArea(registry, areaId);
}
