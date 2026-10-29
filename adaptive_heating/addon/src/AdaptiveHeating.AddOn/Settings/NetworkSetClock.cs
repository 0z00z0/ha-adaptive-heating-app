using System.Reflection;

using AdaptiveHeating.Page.Settings;
using AdaptiveHeating.Planner;

namespace AdaptiveHeating.AddOn.Settings;

/// <summary>
/// Marks a stamp while the box's clock cannot have been set from the network, and announces the moment it
/// can have been.
/// </summary>
/// <remarks>
///     A cabin that loses power comes back on whatever time its hardware kept. A clock reading from before the
///     add-on was built has certainly not been set from the network, because the software did not exist then.
///     A hardware clock holding a plausible but wrong time passes this test.
/// </remarks>
internal sealed class NetworkSetClock : IStampSource
{
	private readonly TimeProvider _clock;
	private readonly DateTimeOffset _notBefore;
	private readonly Lock _gate = new();

	private bool _settled;

	public NetworkSetClock(TimeProvider clock, DateTimeOffset notBefore)
	{
		_clock = clock ?? throw new ArgumentNullException(nameof(clock));
		_notBefore = notBefore;
	}

	/// <summary>Raised once, carrying the moment the clock first read as set.</summary>
	public event Action<DateTimeOffset>? ClockWasSet;

	/// <summary>The moment this build was written to disk, which is the floor a believable clock sits above.</summary>
	/// <remarks>An assembly with no file behind it answers the start of time, so every clock then reads as set.</remarks>
	public static DateTimeOffset BuiltAt(Assembly assembly)
	{
		ArgumentNullException.ThrowIfNull(assembly);

		string location = assembly.Location;

		return location.Length == 0 || !File.Exists(location)
			? DateTimeOffset.MinValue
			: new DateTimeOffset(File.GetLastWriteTimeUtc(location), TimeSpan.Zero);
	}

	/// <inheritdoc/>
	public Stamp Take()
	{
		DateTimeOffset now = _clock.GetUtcNow();

		return HasBeenSet(now) ? Stamp.Certain(now) : Stamp.AgainstAnUnsetClock(now);
	}

	/// <summary>Looks at the clock without writing anything, so the announcement does not wait on somebody saving.</summary>
	public bool Check() => HasBeenSet(_clock.GetUtcNow());

	private bool HasBeenSet(DateTimeOffset now)
	{
		bool crossed;

		lock (_gate)
		{
			if (_settled)
				return true;

			if (now < _notBefore)
				return false;

			_settled = true;
			crossed = true;
		}

		if (crossed)
			ClockWasSet?.Invoke(now);

		return true;
	}
}
