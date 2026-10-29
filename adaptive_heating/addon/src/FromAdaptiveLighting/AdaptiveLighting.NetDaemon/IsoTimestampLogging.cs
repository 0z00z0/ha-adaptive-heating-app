using AdaptiveHeating.AddOn.Logging;
using AdaptiveHeating.Planner;

using Serilog;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

namespace AdaptiveLighting.NetDaemon;

/// <summary>Stamps the log with a full ISO date, and keeps a durable copy where an update does not reach.</summary>
/// <remarks>
///     The add-on is an ASP.NET Core application and not a NetDaemon host: the providers are cleared and Serilog added
///     to the services, and the directory is handed in, not resolved from a lighting document.
///     <para>
///         An add-on log is read days later and across midnight, so the date belongs in every line. The Supervisor's
///         own buffer holds only the newest lines, which is what the durable copy is for.
///     </para>
/// </remarks>
internal static class IsoTimestampLogging
{
	/// <summary>The console template. A full date, because a bare time cannot be placed once a window crosses midnight.</summary>
	// The household's stamp and not Serilog's own {Timestamp}: that one is the process's clock, and it carries no
	// offset, so a line could not be placed against the Supervisor's own at all.
	public const string ConsoleTemplate =
		"[{HouseholdTime} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

	/// <summary>
	/// Replaces the host's logger with one whose timestamps carry the date, and attaches the durable copy where
	/// <paramref name="durableDirectory"/> names somewhere an update keeps.
	/// </summary>
	/// <param name="builder">The host being built, whose own providers are cleared.</param>
	/// <param name="household">The household's clock, which every line is stamped with.</param>
	/// <param name="durableDirectory">Where the durable copy goes, or <c>null</c> for the console alone.</param>
	/// <param name="stem">The settings document's stem, so two documents in one directory cannot collide.</param>
	/// <param name="minimumLevel">The level below which nothing is written.</param>
	/// <returns>The directory the durable copy went to, or <c>null</c> where only the console is logged to.</returns>
	public static string? UseIsoTimestampLogging(
		this WebApplicationBuilder builder,
		TheHouseholdClock household,
		string? durableDirectory,
		string stem,
		LogEventLevel minimumLevel = LogEventLevel.Debug)
	{
		ArgumentNullException.ThrowIfNull(builder);
		ArgumentNullException.ThrowIfNull(household);
		ArgumentException.ThrowIfNullOrWhiteSpace(stem);

		string? attached = Prepare(durableDirectory);

		// The document's LogLevel moves this switch at run time; unset puts minimumLevel back.
		LoggingLevelSwitch levelSwitch = new(minimumLevel);
		SerilogLevelControl.Adopt(levelSwitch);

		// Levels are set here, never read from configuration: this replaces the logger the host built from
		// Logging:LogLevel, and Serilog's own configuration reader wants a section this add-on does not carry,
		// leaving Information and silently dropping every debug line.
		LoggerConfiguration logger = new LoggerConfiguration()
			.MinimumLevel.ControlledBy(levelSwitch)
			.MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
			.Enrich.FromLogContext()
			.Enrich.With(new HouseholdTimestamp(household))
			.WriteTo.Console(outputTemplate: ConsoleTemplate, theme: AnsiConsoleTheme.Code);

		if (attached is { Length: > 0 })
		{
			// The file sink reports a failure to open only through SelfLog, which nothing else here turns on.
			SelfLog.Enable(new LogFailureReport().Write);

			DurableLogFile.AddTo(logger, attached, stem);
		}

		// Cleared first: the providers the web host added stay otherwise, and every line is written twice.
		builder.Logging.ClearProviders();
		builder.Services.AddSerilog(logger.CreateLogger(), dispose: true);

		return attached;
	}

	// The directory is made here rather than by the sink, so a path that cannot be made falls back to the console
	// instead of losing the first lines to a sink that never opens.
	private static string? Prepare(string? durableDirectory)
	{
		if (durableDirectory is not { Length: > 0 })
			return null;

		try
		{
			Directory.CreateDirectory(durableDirectory);

			return durableDirectory;
		}
		catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
		{
			Console.Error.WriteLine($"The durable log directory {durableDirectory} could not be made: {failure.Message}");

			return null;
		}
	}
}
