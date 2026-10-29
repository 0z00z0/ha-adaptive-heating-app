namespace AdaptiveLighting.Web.Presentation;

/// <summary>
///     A page model's per-second signal, for the parts of a design whose words are only honest while they move.
/// </summary>
/// <remarks>
///     The model owns the clock and publishes it; the design decides what listens. Everything that does not
///     move stays off the signal and is drawn again only when a value it shows has actually changed.
/// </remarks>
public interface IPageClock
{
	/// <summary>The page's own moment, so every relative time on one render agrees.</summary>
	DateTimeOffset Now { get; }

	/// <summary>Raised once a second, already on the thread the interface renders on.</summary>
	event Action? Tick;
}
