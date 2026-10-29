using AdaptiveHeating.AddOn.Integration;

namespace AdaptiveHeating.AddOn.Reporting;

/// <summary>Where a report goes in Home Assistant's own record of what happened in the house.</summary>
internal interface ITellsTheLogbook
{
	Task<bool> WriteAsync(string what, string why, string? entityId, CancellationToken token);
}

/// <summary>Writes one line per report into Home Assistant's logbook.</summary>
/// <remarks>The entity id is the room's own, so the Activity panel beside a room shows that room's lines alone.</remarks>
internal sealed class TellsTheLogbook : ITellsTheLogbook
{
	public const string Domain = "logbook";

	public const string Log = "log";

	/// <summary>What every line is attributed to, so a reader can tell the heating's lines from anybody else's.</summary>
	public const string Author = "Adaptive Heating";

	private readonly CoreApi _api;
	private readonly ILogger<TellsTheLogbook> _logger;

	public TellsTheLogbook(CoreApi api, ILogger<TellsTheLogbook> logger)
	{
		_api = api ?? throw new ArgumentNullException(nameof(api));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <inheritdoc/>
	public async Task<bool> WriteAsync(string what, string why, string? entityId, CancellationToken token)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(what);
		ArgumentNullException.ThrowIfNull(why);

		// The target is a field rather than the call's target: logbook.log takes no target and refuses one.
		Dictionary<string, object?> fields = new(StringComparer.Ordinal)
		{
			["name"] = Author,
			["message"] = why is { Length: > 0 } ? $"{what}. {why}" : what
		};

		if (entityId is { Length: > 0 } room)
			fields["entity_id"] = room;

		ActionOutcome outcome = await _api
			.CallAsync(Domain, Log, entityId: null, fields, token)
			.ConfigureAwait(false);

		if (outcome == ActionOutcome.Called)
			return true;

		_logger.LogDebug("{What} did not reach the logbook ({Outcome}); it is still in the add-on's own record.", what, outcome);

		return false;
	}
}
