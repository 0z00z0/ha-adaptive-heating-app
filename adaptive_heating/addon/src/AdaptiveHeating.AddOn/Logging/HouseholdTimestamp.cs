using System.Globalization;

using AdaptiveHeating.Planner;

using AdaptiveLighting.NetDaemon;

using Serilog.Core;
using Serilog.Events;

namespace AdaptiveHeating.AddOn.Logging;

/// <summary>Stamps every log event with the household's own wall clock, for the console and for the durable file.</summary>
/// <remarks>
///     Serilog's own <c>{Timestamp}</c> is the process's clock, which in an add-on container with no zone database is
///     UTC however the household's zone is named. An enricher rather than a sink wrapper, so both sinks read the same
///     property and the console keeps its colour theme.
///     <para>
///         Both stamps are written invariantly: a log line is read by a machine as often as by a person, and a
///         locale-formatted date in a file read on another machine cannot be parsed back.
///     </para>
/// </remarks>
internal sealed class HouseholdTimestamp : ILogEventEnricher
{
	/// <summary>The console's stamp: the date, the time and the offset, so a line can be placed against any other clock.</summary>
	public const string TimeProperty = "HouseholdTime";

	/// <summary>The durable file's stamp, which keeps its milliseconds.</summary>
	public const string StampProperty = "HouseholdStamp";

	private const string TimeFormat = "yyyy-MM-dd HH:mm:sszzz";

	private readonly TheHouseholdClock _household;

	public HouseholdTimestamp(TheHouseholdClock household) =>
		_household = household ?? throw new ArgumentNullException(nameof(household));

	/// <inheritdoc/>
	public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
	{
		ArgumentNullException.ThrowIfNull(logEvent);
		ArgumentNullException.ThrowIfNull(propertyFactory);

		DateTimeOffset wall = _household.Wall(logEvent.Timestamp);

		logEvent.AddPropertyIfAbsent(
			propertyFactory.CreateProperty(TimeProperty, wall.ToString(TimeFormat, CultureInfo.InvariantCulture)));

		logEvent.AddPropertyIfAbsent(
			propertyFactory.CreateProperty(
				StampProperty,
				wall.ToString(DurableLogFormatter.TimestampFormat, CultureInfo.InvariantCulture)));
	}
}
