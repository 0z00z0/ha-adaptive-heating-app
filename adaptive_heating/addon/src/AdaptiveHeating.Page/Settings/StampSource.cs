using AdaptiveHeating.Planner;

namespace AdaptiveHeating.Page.Settings;

/// <summary>Where a change gets the moment it was made, and whether the clock behind it can be believed.</summary>
public interface IStampSource
{
	Stamp Take();
}

/// <summary>Stamps from a clock nothing watches, so every one reads as made with a good clock.</summary>
/// <remarks>The render host and any caller with no box under it use this. The add-on uses the watcher instead.</remarks>
public sealed class CertainStamps : IStampSource
{
	private readonly TimeProvider _clock;

	public CertainStamps(TimeProvider clock) =>
		_clock = clock ?? throw new ArgumentNullException(nameof(clock));

	/// <inheritdoc/>
	public Stamp Take() => Stamp.Certain(_clock.GetUtcNow());
}
