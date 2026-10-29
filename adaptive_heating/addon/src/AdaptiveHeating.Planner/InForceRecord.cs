namespace AdaptiveHeating.Planner;

/// <summary>A change that arrived carrying an older stamp than the one already held.</summary>
public sealed record RefusedChange(string RoomId, double ArrivingTemperature, Stamp ArrivingStamp, double TemperatureHeld, Stamp StampHeld);

public sealed record ArrivingTemperature(string RoomId, Stamped<double> Temperature);

public sealed record MergeOutcome(InForceRecord Record, IReadOnlyList<RefusedChange> Refused);

/// <summary>The mode and the temperature per room, each number carrying the moment it was set.</summary>
public sealed record InForceRecord
{
	public static InForceRecord Empty { get; } = new();

	public Stamped<string>? Mode { get; init; }

	public IReadOnlyDictionary<string, Stamped<double>> Temperatures { get; init; } =
		new Dictionary<string, Stamped<double>>(StringComparer.Ordinal);

	public bool WouldAccept(string roomId, Stamped<double> arriving) =>
		!Temperatures.TryGetValue(roomId, out Stamped<double> held) || held.IsReplacedBy(arriving);

	public InForceRecord Receive(string roomId, Stamped<double> arriving) =>
		WouldAccept(roomId, arriving)
			? this with { Temperatures = With(Temperatures, roomId, arriving) }
			: this;

	public MergeOutcome Merge(IEnumerable<ArrivingTemperature> arriving)
	{
		ArgumentNullException.ThrowIfNull(arriving);

		Dictionary<string, Stamped<double>> merged = new(Temperatures, StringComparer.Ordinal);
		List<RefusedChange> refused = [];

		foreach (ArrivingTemperature change in arriving)
		{
			if (!merged.TryGetValue(change.RoomId, out Stamped<double> held))
			{
				merged[change.RoomId] = change.Temperature;
				continue;
			}

			if (held.IsReplacedBy(change.Temperature))
			{
				merged[change.RoomId] = change.Temperature;
			}
			else
			{
				refused.Add(new RefusedChange(change.RoomId, change.Temperature.Value, change.Temperature.Stamp, held.Value, held.Stamp));
			}
		}

		return new MergeOutcome(this with { Temperatures = merged }, refused);
	}

	/// <summary>
	/// Rewrites every stamp written against an unset clock to the moment the clock was set. A change
	/// made during an outage then stands newer than anything before it and older than anything after.
	/// </summary>
	public InForceRecord WhenTheClockIsSet(DateTimeOffset networkTime)
	{
		Dictionary<string, Stamped<double>> rewritten = new(StringComparer.Ordinal);
		foreach (KeyValuePair<string, Stamped<double>> entry in Temperatures)
		{
			rewritten[entry.Key] = entry.Value.WhenTheClockIsSet(networkTime);
		}

		return this with
		{
			Mode = Mode?.WhenTheClockIsSet(networkTime),
			Temperatures = rewritten,
		};
	}

	private static Dictionary<string, Stamped<double>> With(
		IReadOnlyDictionary<string, Stamped<double>> temperatures,
		string roomId,
		Stamped<double> temperature)
	{
		Dictionary<string, Stamped<double>> next = new(temperatures, StringComparer.Ordinal);
		next[roomId] = temperature;
		return next;
	}
}
