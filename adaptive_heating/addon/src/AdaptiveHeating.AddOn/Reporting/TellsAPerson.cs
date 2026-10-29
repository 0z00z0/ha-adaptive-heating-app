using AdaptiveHeating.AddOn.Integration;

namespace AdaptiveHeating.AddOn.Reporting;

/// <summary>How the add-on tells a person something it cannot put right itself.</summary>
internal interface ITellsAPerson
{
	// The kind, in snake case, and the room id (null for the house) name the card; the words never do.
	Task<bool> NotifyAsync(string kind, string? roomId, string title, string message, CancellationToken token);
}

/// <summary>Raises a card in Home Assistant.</summary>
/// <remarks>
///     Taken from the lighting engine's <c>HaNotifier</c> and its <c>NotifyPersistent</c> extension, read on
///     2026-09-25. What changed: the call goes over this add-on's own websocket rather than through NetDaemon's
///     entity model, and it is awaited, because the caller already runs on a pass it can wait inside.
///     <para>
///         <c>persistent_notification.create</c>, since <c>notify.persistent_notification</c> takes no notification
///         id. The id is the kind and the room id, never the words, so a new temperature or a renamed room
///         replaces the room's card instead of stacking one.
///     </para>
/// </remarks>
internal sealed class TellsAPerson : ITellsAPerson
{
	public const string Domain = "persistent_notification";

	public const string Create = "create";

	/// <summary>What every card this add-on raises is named by, so none of them collides with another add-on's.</summary>
	public const string NotificationIdPrefix = "adaptive_heating_";

	private readonly CoreApi _api;
	private readonly ILogger<TellsAPerson> _logger;

	public TellsAPerson(CoreApi api, ILogger<TellsAPerson> logger)
	{
		_api = api ?? throw new ArgumentNullException(nameof(api));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <summary>The card's id: the kind and the room id, or <c>house</c> where no room is named.</summary>
	public static string IdFor(string kind, string? roomId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(kind);

		return $"{NotificationIdPrefix}{kind}_{(roomId is { Length: > 0 } ? roomId : "house")}";
	}

	/// <inheritdoc/>
	public async Task<bool> NotifyAsync(string kind, string? roomId, string title, string message, CancellationToken token)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(title);
		ArgumentNullException.ThrowIfNull(message);

		ActionOutcome outcome = await _api.CallAsync(
			Domain,
			Create,
			entityId: null,
			new Dictionary<string, object?>(StringComparer.Ordinal)
			{
				["title"] = title,
				["message"] = message,
				["notification_id"] = IdFor(kind, roomId)
			},
			token).ConfigureAwait(false);

		if (outcome == ActionOutcome.Called)
			return true;

		_logger.LogWarning("{Title} did not reach Home Assistant as a card ({Outcome}); it is still in the record.",
			title, outcome);

		return false;
	}
}
