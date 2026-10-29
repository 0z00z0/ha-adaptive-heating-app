using System.Threading;

namespace AdaptiveLighting.NetDaemon;

/// <summary>Puts Serilog's own failures somewhere a person reads, without repeating one line per lost event.</summary>
/// <remarks>
///     A failing sink reports only through <c>Serilog.Debugging.SelfLog</c>, which is off unless turned on. The file
///     sink reports per event, and a house at the measured rate loses around a dozen a minute, each with a stack
///     trace. A repeat of the same message is held back for <see cref="RepeatAfter"/>; a different failure never is.
/// </remarks>
internal sealed class LogFailureReport
{
	/// <summary>How long the same message is held back before it is worth saying again.</summary>
	public static readonly TimeSpan RepeatAfter = TimeSpan.FromMinutes(5);

	private readonly Action<string> _report;
	private readonly Func<DateTimeOffset> _now;
	private readonly Lock _gate = new();

	private string? _last;
	private DateTimeOffset _lastAt;

	/// <summary>Creates a reporter over a destination, standard error by default.</summary>
	/// <param name="report">Where a failure is announced. Never an <c>ILogger</c>, or the failure loops back here.</param>
	/// <param name="now">The clock, so a test does not have to wait out <see cref="RepeatAfter"/>.</param>
	public LogFailureReport(Action<string>? report = null, Func<DateTimeOffset>? now = null)
	{
		_report = report ?? Console.Error.WriteLine;
		_now = now ?? (() => DateTimeOffset.UtcNow);
	}

	/// <summary>Takes one line as <c>SelfLog</c> hands it over, and passes on the ones worth reading.</summary>
	public void Write(string message)
	{
		ArgumentNullException.ThrowIfNull(message);

		string body = Body(message);
		DateTimeOffset at = _now();

		lock (_gate)
		{
			if (string.Equals(_last, body, StringComparison.Ordinal) && at - _lastAt < RepeatAfter)
				return;

			_last = body;
			_lastAt = at;
		}

		_report(message.TrimEnd());
	}

	// SelfLog prefixes a round-trip timestamp, which differs on every repeat and would defeat the comparison.
	private static string Body(string message)
	{
		int space = message.IndexOf(' ', StringComparison.Ordinal);

		return space < 0 ? message.Trim() : message[(space + 1)..].Trim();
	}
}
