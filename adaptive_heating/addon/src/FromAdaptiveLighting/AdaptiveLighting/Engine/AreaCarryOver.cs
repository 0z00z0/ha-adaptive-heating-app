using AdaptiveLighting.Engine;

namespace Adaptive.Engine;

/// <summary>What a room shows of its recent past. Read by people, never by a rule.</summary>
// A person's change is kept by ChangedByUserId, an automation's by ChangedByAutomationId, with no words;
// ChangedBy holds words only for other origins.
public sealed record AreaHistory(
	DateTimeOffset? LastMotionAt,
	DateTimeOffset? ChangedAt,
	string? ChangedBy,
	string? ChangedByUserId = null,
	string? ChangedByAutomationId = null);
