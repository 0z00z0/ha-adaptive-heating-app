namespace AdaptiveHeating.Page;

/// <summary>One entry in the theme picker.</summary>
// Id is stored in the browser and carried as data-theme: add themes, never rename them.
public sealed record HeatingTheme(string Id, string NameKey, bool IsDark);

/// <summary>Lamplight's six palettes and the device-following default, under lighting's own ids.</summary>
public static class HeatingThemes
{
	public static readonly HeatingTheme System = new("system", "ThemeFollowsDevice", false);

	public static readonly HeatingTheme WarmCharcoal = new("warm-charcoal", "ThemeWarmCharcoal", true);

	public static readonly HeatingTheme PlumDusk = new("plum-dusk", "ThemePlumDusk", true);

	public static readonly HeatingTheme Blackout = new("blackout", "ThemeBlackout", true);

	public static readonly HeatingTheme ZeroZero = new("0z0", "ThemeZeroZero", true);

	public static readonly HeatingTheme Paper = new("paper", "ThemePaper", false);

	public static readonly HeatingTheme Fern = new("fern", "ThemeFern", false);

	public static IReadOnlyList<HeatingTheme> All { get; } =
		[System, WarmCharcoal, PlumDusk, Blackout, ZeroZero, Paper, Fern];

	public static HeatingTheme DarkDefault => WarmCharcoal;

	public static HeatingTheme LightDefault => Paper;

	/// <summary>The head script's allow-list. System is the absence of a stored choice, not a value.</summary>
	public static string DataThemeIds { get; } =
		string.Join(' ', All.Where(theme => theme != System).Select(theme => theme.Id));

	/// <summary>The theme a stored id names, or <see cref="System"/> when it names nothing this build ships.</summary>
	public static HeatingTheme Resolve(string? storedId) =>
		All.FirstOrDefault(theme => string.Equals(theme.Id, storedId, StringComparison.Ordinal)) ?? System;
}
