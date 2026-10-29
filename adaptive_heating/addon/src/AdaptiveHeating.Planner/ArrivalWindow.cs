namespace AdaptiveHeating.Planner;

/// <summary>
/// The arrivals read off a calendar over one stretch of time, and which of them a warm-up plans backwards from.
/// </summary>
/// <param name="ReadAt">When the reading was taken.</param>
/// <param name="Until">The far end of the stretch that was read.</param>
/// <param name="Arrivals">Every arrival in it, earliest first.</param>
public sealed record ArrivalWindow(DateTimeOffset ReadAt, DateTimeOffset Until, IReadOnlyList<DateTimeOffset> Arrivals)
{
	/// <summary>
	/// How far ahead arrivals are read. Long enough that the longest warm-up a room can plan still starts inside
	/// it: a room on the lowest rate it begins with, 0.5 °C an hour planned at four fifths of that, takes forty
	/// hours to rise sixteen degrees.
	/// </summary>
	// Nothing is spent on the length: only the nearest arrival is planned against, and a plan refuses to start
	// until its own moment comes round.
	public static readonly TimeSpan Length = TimeSpan.FromDays(14);

	/// <summary>How long a reading stands before it is taken again.</summary>
	/// <remarks>A pass runs once a minute and an arrival changes when a person edits a calendar, so reading once
	/// per pass would cost a call a minute for an answer that hardly moves. A change this misses by a quarter of
	/// an hour is a change made too late to plan a warm-up around.</remarks>
	public static readonly TimeSpan StandsFor = TimeSpan.FromMinutes(15);

	/// <summary>No calendar has been read, which is not the same as a calendar holding no arrival.</summary>
	public static ArrivalWindow Unread { get; } = new(default, default, []);

	/// <summary>Whether a reading has been taken at all.</summary>
	public bool WasRead => Until > ReadAt;

	/// <summary>Whether the reading is old enough to be worth taking again.</summary>
	public bool IsStaleAt(DateTimeOffset now) => !WasRead || now - ReadAt >= StandsFor;

	/// <summary>The next arrival after <paramref name="now"/>, or <c>null</c> where none is ahead.</summary>
	/// <remarks>An arrival already reached is no deadline: a warm-up planned backwards from it would have no time
	/// to run at all.</remarks>
	public DateTimeOffset? Next(DateTimeOffset now)
	{
		DateTimeOffset? nearest = null;

		foreach (DateTimeOffset arrival in Arrivals)
		{
			if (arrival > now && (nearest is not { } held || arrival < held))
				nearest = arrival;
		}

		return nearest;
	}

	/// <summary>The reading as it stands, with the arrivals put in order.</summary>
	public static ArrivalWindow Of(DateTimeOffset readAt, DateTimeOffset until, IEnumerable<DateTimeOffset> arrivals)
	{
		ArgumentNullException.ThrowIfNull(arrivals);

		return new ArrivalWindow(readAt, until, [.. arrivals.Order()]);
	}
}
