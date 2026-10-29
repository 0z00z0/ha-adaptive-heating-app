namespace AdaptiveHeating.Planner;

/// <summary>What chose the temperature a room is holding.</summary>
public enum TargetSource
{
	ModeTemperature,
	HandChange,
	TimedHold,
	WarmUp,
}

/// <summary>
/// The temperatures offered for one room. A timed hold that has expired and a warm-up that is not
/// running are left out by the caller rather than carried here.
/// </summary>
public sealed record RoomTargetCandidates
{
	public required Stamped<double> ModeTemperature { get; init; }

	public Stamped<double>? HandChange { get; init; }

	public Stamped<double>? TimedHold { get; init; }

	public Stamped<double>? WarmUp { get; init; }
}

/// <summary>The temperature that won, and where and by whom it was set.</summary>
public sealed record ResolvedTarget(double Temperature, TargetSource Source, Stamp Stamp)
{
	public ChangeOrigin Origin { get; init; }

	public SetBy By { get; init; }
}

/// <summary>
/// One temperature per room. A running warm-up wins outright and an unexpired hold comes next; below
/// those the more recently set of the mode temperature and a hand change wins.
/// </summary>
public static class Precedence
{
	public static ResolvedTarget Resolve(RoomTargetCandidates candidates)
	{
		ArgumentNullException.ThrowIfNull(candidates);

		if (candidates.WarmUp is Stamped<double> warmUp)
		{
			return Won(warmUp, TargetSource.WarmUp);
		}

		if (candidates.TimedHold is Stamped<double> hold)
		{
			return Won(hold, TargetSource.TimedHold);
		}

		Stamped<double> standing = candidates.ModeTemperature;
		TargetSource source = TargetSource.ModeTemperature;

		if (candidates.HandChange is Stamped<double> byHand && standing.IsReplacedBy(byHand))
		{
			standing = byHand;
			source = TargetSource.HandChange;
		}

		return Won(standing, source);
	}

	private static ResolvedTarget Won(Stamped<double> winner, TargetSource source) =>
		new(winner.Value, source, winner.Stamp) { Origin = winner.Origin, By = winner.By };
}
