using AdaptiveLighting.Abstractions;

using Microsoft.Extensions.Logging;

using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace AdaptiveLighting.NetDaemon;

/// <summary>Moves the level switch <see cref="IsoTimestampLogging"/> built the logger on.</summary>
/// <remarks>Unsupported in a house that builds its own logging, which then keeps whatever level it set.</remarks>
internal sealed class SerilogLevelControl : ILogLevelControl
{
	// One per process, set when IsoTimestampLogging's logger is built. Read on every call, so registration order does not matter.
	private static LoggingLevelSwitch? s_switch;
	private static LogEventLevel s_initial;

	internal static void Adopt(LoggingLevelSwitch levelSwitch)
	{
		s_switch = levelSwitch;
		s_initial = levelSwitch.MinimumLevel;
	}

	public bool IsSupported => s_switch is not null;

	public void Apply(LogLevel level)
	{
		if (s_switch is { } levelSwitch)
			levelSwitch.MinimumLevel = LevelConvert.ToSerilogLevel(level);
	}

	public void RestoreInitial()
	{
		if (s_switch is { } levelSwitch)
			levelSwitch.MinimumLevel = s_initial;
	}
}
