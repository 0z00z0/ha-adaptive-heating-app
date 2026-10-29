using System.Globalization;
using System.Text;

using AdaptiveHeating.AddOn.Logging;

using Serilog.Events;
using Serilog.Formatting;
using Serilog.Parsing;

namespace AdaptiveLighting.NetDaemon;

/// <summary>Renders one log event for the durable file, from the template instead of the message.</summary>
/// <remarks>
///     Never calls <c>LogEvent.RenderMessage</c>, which returns the interpolated string, secrets and all. Rendering from
///     <see cref="MessageTemplate.Tokens"/> sends every value through <see cref="LoggedValue"/>. Format specifiers and
///     alignment are dropped: honouring them would run an unseen formatter on a value this type bounds.
/// </remarks>
internal sealed class DurableLogFormatter : ITextFormatter
{
	/// <summary>Full ISO date, and the offset with it: this file is read days later and across midnight.</summary>
	public const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fffzzz";

	/// <summary>How much of an exception a line will carry, stack trace included.</summary>
	public const int MaxExceptionLength = 2000;

	/// <summary>What one line may reach, so no single event can approach the file's size limit.</summary>
	public const int MaxLineChars = 4096;

	private const string SourceContextProperty = "SourceContext";
	private const int MaxTemplateTextLength = 1024;
	private const string Ellipsis = "...";

	/// <summary>Writes the rendered line, which the file sink counts towards its size limit.</summary>
	public void Format(LogEvent logEvent, TextWriter output)
	{
		ArgumentNullException.ThrowIfNull(logEvent);
		ArgumentNullException.ThrowIfNull(output);

		output.WriteLine(Render(logEvent));
	}

	/// <summary>The one line <paramref name="logEvent"/> becomes.</summary>
	public static string Render(LogEvent logEvent)
	{
		ArgumentNullException.ThrowIfNull(logEvent);

		StringBuilder line = new(256);

		// The household's wall clock where the enricher stamped one: the container's own clock is UTC.
		line.Append(logEvent.Properties.TryGetValue(HouseholdTimestamp.StampProperty, out LogEventPropertyValue? stamp)
				? LoggedValue.Of(HouseholdTimestamp.StampProperty, stamp)
				: logEvent.Timestamp.ToString(TimestampFormat, CultureInfo.InvariantCulture))
			.Append(' ')
			.Append(Abbreviate(logEvent.Level))
			.Append(' ')
			.Append(SourceContext(logEvent))
			.Append(" | ");

		foreach (MessageTemplateToken token in logEvent.MessageTemplate.Tokens)
			line.Append(Rendered(token, logEvent));

		if (logEvent.Exception is { } exception)
			line.Append(" | ").Append(LoggedValue.Text(exception.ToString(), MaxExceptionLength));

		return Cap(line.ToString());
	}

	// The file sink writes the last event within its size limit in full, so the cap here is what bounds the
	// overshoot past DurableLogFile.MaxFileBytes.
	private static string Cap(string line)
	{
		if (line.Length <= MaxLineChars)
			return line;

		int cut = MaxLineChars;

		// Never split a surrogate pair; a lone half encodes as U+FFFD and reads as corruption.
		if (char.IsHighSurrogate(line[cut - 1]))
			cut--;

		return string.Concat(line.AsSpan(0, cut), Ellipsis);
	}

	private static string Rendered(MessageTemplateToken token, LogEvent logEvent) =>
		token switch
		{
			PropertyToken property => logEvent.Properties.TryGetValue(property.PropertyName, out LogEventPropertyValue? value)
				? LoggedValue.Of(property.PropertyName, value)
				: "{" + property.PropertyName + "}",
			TextToken text => LoggedValue.Text(text.Text, MaxTemplateTextLength),
			_ => string.Empty
		};

	private static string SourceContext(LogEvent logEvent) =>
		logEvent.Properties.TryGetValue(SourceContextProperty, out LogEventPropertyValue? value)
			? LoggedValue.Of(SourceContextProperty, value)
			: "-";

	// Serilog's own {Level:u3}, so the two logs read the same way.
	private static string Abbreviate(LogEventLevel level) =>
		level switch
		{
			LogEventLevel.Verbose => "VRB",
			LogEventLevel.Debug => "DBG",
			LogEventLevel.Information => "INF",
			LogEventLevel.Warning => "WRN",
			LogEventLevel.Error => "ERR",
			_ => "FTL"
		};
}
