using System.Text.Json;

namespace AdaptiveHeating.AddOn.Integration;

/// <summary>What the registry says about one entity, and the one place a consumer reads any of it from.</summary>
internal sealed record RegistryAnswer(
	string EntityId,
	string? AreaId,
	IReadOnlyList<RegistryLabel> Labels,
	string? DeviceId,
	bool Hidden,
	string? Platform)
{
	public static RegistryAnswer NothingKnownAbout(string entityId) => new(entityId, null, [], null, false, null);
}

/// <summary>Which entities a person has hidden, read from Home Assistant's own entity registry.</summary>
// The entity model carries no visibility member, so this one value cannot come with the other three.
internal sealed class WhatIsHidden
{
	/// <summary>The full registry list, not the display form: only this one carries <c>hidden_by</c>.</summary>
	public const string Command = "config/entity_registry/list";

	private const string EntityIdField = "entity_id";
	private const string HiddenByField = "hidden_by";

	private readonly CoreConnection _connection;
	private readonly ILogger<WhatIsHidden> _logger;

	private HashSet<string> _hidden = new(StringComparer.Ordinal);
	private int _readOnConnection = -1;

	public WhatIsHidden(CoreConnection connection, ILogger<WhatIsHidden> logger)
	{
		_connection = connection ?? throw new ArgumentNullException(nameof(connection));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <summary>Whether <paramref name="entityId"/> was hidden as of the last reading.</summary>
	public bool Hidden(string entityId) => _hidden.Contains(entityId);

	/// <summary>Reads the registry where this connection has not been read yet.</summary>
	public async Task ReadAsync(CancellationToken token)
	{
		int opened = _connection.TimesOpened;

		if (_readOnConnection == opened)
			return;

		ReplyFromTheCore reply = await _connection.AskAsync(Command, token).ConfigureAwait(false);

		// A read that did not go through leaves the marker where it was, so the next ask tries again.
		if (!reply.Succeeded || reply.Result is not { } answer)
		{
			_logger.LogDebug("The entity registry could not be read ({Code}), so no entity counts as hidden yet.", reply.Code);

			return;
		}

		_hidden = HiddenIn(answer);
		_readOnConnection = opened;

		_logger.LogInformation("Home Assistant reports {Count} hidden entity/entities.", _hidden.Count);
	}

	/// <summary>Every hidden entity in the answer. An entry the wrong shape is skipped, so one odd row costs nothing.</summary>
	public static HashSet<string> HiddenIn(JsonElement answer)
	{
		HashSet<string> hidden = new(StringComparer.Ordinal);

		if (answer.ValueKind != JsonValueKind.Array)
			return hidden;

		foreach (JsonElement entry in answer.EnumerateArray())
		{
			if (entry.ValueKind != JsonValueKind.Object)
				continue;

			if (!entry.TryGetProperty(HiddenByField, out JsonElement by) || by.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
				continue;

			if (entry.TryGetProperty(EntityIdField, out JsonElement id)
				&& id.ValueKind == JsonValueKind.String
				&& id.GetString() is { Length: > 0 } entityId)
			{
				hidden.Add(entityId);
			}
		}

		return hidden;
	}
}
