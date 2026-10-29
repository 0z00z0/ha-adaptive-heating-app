using System.Globalization;
using System.Text.Json;

namespace AdaptiveHeating.AddOn.Integration;

/// <summary>What a call to an action did.</summary>
internal enum ActionOutcome
{
	Called,

	/// <summary>The integration does not have that action: it is older, or it failed to load. Not fatal.</summary>
	UnknownAction,

	/// <summary>The thermostat would not do it and said why. An answer from the room, not a fault in the add-on.</summary>
	Refused,

	/// <summary>Anything else: no connection, no answer, a bad field. The call is lost and the caller carries on.</summary>
	Failed
}

/// <summary>What the add-on asks of Home Assistant, over the one connection the Supervisor proxies.</summary>
/// <remarks>
///     No user and no long-lived token exist anywhere. An action goes over the open websocket, which carries
///     back the reason it failed. An unknown action is reported once and then counted without a line, because a
///     house running an older integration would otherwise fill its log.
/// </remarks>
internal sealed class CoreApi
{
	private readonly CoreConnection _connection;
	private readonly ILogger<CoreApi> _logger;
	private readonly HashSet<string> _alreadyReported = new(StringComparer.Ordinal);
	private readonly Lock _gate = new();

	public CoreApi(CoreConnection connection, ILogger<CoreApi> logger)
	{
		_connection = connection ?? throw new ArgumentNullException(nameof(connection));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <summary>The token the Supervisor puts in the add-on's environment, or <c>null</c> where the add-on is not under one.</summary>
	public static string? SupervisorToken() => Environment.GetEnvironmentVariable("SUPERVISOR_TOKEN");

	/// <summary>The household's IANA zone id, as the Supervisor puts it in the container, or <c>null</c> where none is set.</summary>
	/// <remarks>
	///     The add-on developer documentation names no variable for this; <c>TZ</c> is what the Supervisor sets, from
	///     its own timezone value, for every add-on container and for Home Assistant's own. It is read rather than
	///     taken from <see cref="TimeZoneInfo.Local"/>, because that resolves from the same variable and falls back to
	///     UTC without saying so.
	/// </remarks>
	public static string? SupervisorZone() => Environment.GetEnvironmentVariable("TZ");

	/// <summary>Calls one action. Never throws: a lost call costs the call and nothing else.</summary>
	/// <param name="entityId">The entity it is called against, or <c>null</c> for an action that takes no target.</param>
	public async Task<ActionOutcome> CallAsync(
		string domain,
		string action,
		string? entityId,
		IReadOnlyDictionary<string, object?> fields,
		CancellationToken token) =>
		(await CallAsync(domain, action, entityId, fields, wantsAnAnswer: false, token).ConfigureAwait(false)).Outcome;

	/// <summary>Calls one action and carries its answer back. Never throws, exactly as the call without one.</summary>
	/// <returns>What the call did, and what Home Assistant answered with where it answered at all.</returns>
	public async Task<(ActionOutcome Outcome, JsonElement? Answer)> CallAsync(
		string domain,
		string action,
		string? entityId,
		IReadOnlyDictionary<string, object?> fields,
		bool wantsAnAnswer,
		CancellationToken token)
	{
		(ActionOutcome outcome, JsonElement? answer, _) =
			await Call(domain, action, entityId, fields, wantsAnAnswer, token).ConfigureAwait(false);

		return (outcome, answer);
	}

	/// <summary>Calls one action and carries back the sentence Home Assistant refused it with. Never throws.</summary>
	public async Task<(ActionOutcome Outcome, string? Said)> CallSayingWhyAsync(
		string domain,
		string action,
		string? entityId,
		IReadOnlyDictionary<string, object?> fields,
		CancellationToken token)
	{
		(ActionOutcome outcome, _, string? said) =
			await Call(domain, action, entityId, fields, wantsAnAnswer: false, token).ConfigureAwait(false);

		return (outcome, said);
	}

	private async Task<(ActionOutcome Outcome, JsonElement? Answer, string? Said)> Call(
		string domain,
		string action,
		string? entityId,
		IReadOnlyDictionary<string, object?> fields,
		bool wantsAnAnswer,
		CancellationToken token)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(domain);
		ArgumentException.ThrowIfNullOrWhiteSpace(action);
		ArgumentNullException.ThrowIfNull(fields);

		Dictionary<string, object?> given = new(StringComparer.Ordinal);

		foreach ((string name, object? value) in fields)
		{
			if (value is not null)
				given[name] = value;
		}

		ReplyFromTheCore reply;

		try
		{
			reply = await _connection.CallAsync(domain, action, entityId, given, wantsAnAnswer, token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			return (ActionOutcome.Failed, null, null);
		}

		if (reply.Succeeded)
			return (ActionOutcome.Called, reply.Result, null);

		return (Refusal(domain, action, entityId, reply), null, reply.Said);
	}

	private ActionOutcome Refusal(string domain, string action, string? entityId, ReplyFromTheCore reply)
	{
		switch (reply.Code)
		{
			case HomeAssistantErrors.NotFound:
				ReportOnce($"{domain}.{action}");

				return ActionOutcome.UnknownAction;

			case HomeAssistantErrors.ServiceValidation:
				_logger.LogInformation(
					"{Domain}.{Action} on {Entity} was refused: {Said}", domain, action, entityId, reply.Said);

				return ActionOutcome.Refused;

			default:
				_logger.LogWarning(
					"{Domain}.{Action} on {Entity} did not go through ({Code}): {Said} The call is lost and nothing is queued.",
					domain, action, entityId, reply.Code, reply.Said);

				return ActionOutcome.Failed;
		}
	}

	private void ReportOnce(string action)
	{
		bool first;

		lock (_gate)
			first = _alreadyReported.Add(action);

		if (first)
			_logger.LogWarning(
				"Home Assistant does not have the {Action} action, so everything resting on it stands unused. "
				+ "The rest of the heating carries on, and this is not said again.",
				action);
		else
			_logger.LogDebug("{Action} is still unknown to Home Assistant.", action);
	}

	/// <summary>Formats a time the way the integration reads one, with no fractional seconds and its own offset.</summary>
	public static string AsTime(DateTimeOffset moment) =>
		moment.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
}
