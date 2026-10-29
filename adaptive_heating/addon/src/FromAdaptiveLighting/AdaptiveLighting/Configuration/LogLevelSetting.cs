namespace AdaptiveLighting.Configuration;

/// <summary>The words <see cref="GlobalConfig.LogLevel"/> accepts, and the level each names.</summary>
public static class LogLevelSetting
{
	public static IReadOnlyList<string> Accepted { get; } = ["Debug", "Information", "Warning"];

	/// <summary>The level <paramref name="text"/> names, ignoring case, or <c>null</c> when unset or not accepted.</summary>
	public static LogLevel? Read(string? text) =>
		Accepted.FirstOrDefault(word => string.Equals(word, text?.Trim(), StringComparison.OrdinalIgnoreCase)) switch
		{
			"Debug" => LogLevel.Debug,
			"Information" => LogLevel.Information,
			"Warning" => LogLevel.Warning,
			_ => null
		};
}
