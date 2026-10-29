namespace Adaptive.Engine;

/// <summary>When each Home Assistant entity was last genuinely heard from.</summary>
/// <remarks>Null means unknown throughout this interface, never dead. A deleted cache or a recent restart gives null.</remarks>
public interface IEntityLastSeen
{
	/// <remarks>UTC, on Home Assistant's clock, so it is comparable with what the Home Assistant UI shows.</remarks>
	DateTimeOffset? LastSeenUtc(string entityId);

	TimeSpan? SilenceOf(string entityId);

	/// <remarks>False when the silence is unknown, and for a non-positive threshold.</remarks>
	bool HasBeenSilentFor(string entityId, TimeSpan threshold);

	/// <summary>When Home Assistant is believed to have last started, or <c>null</c> when no restart has been observed.</summary>
	/// <remarks>Estimated from the entity population; moves forwards only.</remarks>
	DateTimeOffset? HomeAssistantStartedUtc { get; }

	/// <summary>How many entities are currently tracked; zero before the first census.</summary>
	int TrackedCount { get; }
}
