using System.Reactive.Disposables;
using System.Text.Json;

using NetDaemon.HassModel.Entities;

namespace Adaptive.Engine;

/// <summary>Names whoever caused a light change, as closely as Home Assistant's own logbook does.</summary>
// A label and nothing more: whether a change is manual is OverrideDetector's question, never this one's.
// Automations are named off the automation_triggered event carrying the change's context id, then its parent's
// (one level, as the logbook looks). People are named off the person entity whose user_id attribute matches,
// which any token reads; the auth user list needs an administrator.
public sealed class ChangeOriginNames : IDisposable
{
	public const string AutomationTriggeredEvent = "automation_triggered";

	public const string ByTheEngine = "By adaptive heating";

	public const string UnknownSource = "Unknown source";

	// The change arrives right behind its event, so this only has to outlast a burst.
	internal const int RememberedRuns = 256;

	private const string PersonDomain = "person";
	private const string UserIdAttribute = "user_id";
	private const string FriendlyNameAttribute = "friendly_name";

	private readonly IHaContext _ha;
	private readonly ILogger _logger;
	private readonly Lock _gate = new();
	private readonly Dictionary<string, string> _automationByContext = new(StringComparer.Ordinal);
	private readonly Queue<string> _remembered = new();
	private readonly IDisposable _subscription;

	public ChangeOriginNames(IHaContext ha, ILogger logger)
	{
		_ha = ha ?? throw new ArgumentNullException(nameof(ha));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));

		try
		{
			_subscription = ha.Events
				.Where(@event => string.Equals(@event.EventType, AutomationTriggeredEvent, StringComparison.Ordinal))
				.SubscribeSafe(Remember, logger);
		}
		catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
		{
			logger.LogWarning(exception, "Could not watch automation runs, so a change an automation makes is not named.");
			_subscription = Disposable.Empty;
		}
	}

	public const string ByAnAutomation = "By an automation";

	public const string ByAHomeAssistantUser = "By a Home Assistant user";

	/// <summary>Who caused a change, in the activity log's words, or <c>null</c> for an origin with no words.</summary>
	public string? Describe(ChangeOrigin origin, Context? context) => origin switch
	{
		ChangeOrigin.Self => ByTheEngine,
		ChangeOrigin.PhysicalDevice => UnknownSource,
		ChangeOrigin.Automation => AutomationEntityId(context) is { } automation ? AutomationWords(automation) : ByAnAutomation,
		ChangeOrigin.HaUser => PersonName(context?.UserId) is { } person ? $"By {person}" : ByAHomeAssistantUser,
		_ => null
	};

	private void Remember(Event @event)
	{
		if (@event.Context?.Id is not { Length: > 0 } contextId || EntityIdOf(@event) is not { } automation)
			return;

		lock (_gate)
		{
			if (_automationByContext.TryAdd(contextId, automation))
				_remembered.Enqueue(contextId);
			else
				_automationByContext[contextId] = automation;

			while (_remembered.Count > RememberedRuns)
				_automationByContext.Remove(_remembered.Dequeue());
		}
	}

	// The automation is kept by entity id; its name is read when shown, so a rename reads as the new name.
	private static string? EntityIdOf(Event @event) =>
		@event.DataElement is { ValueKind: JsonValueKind.Object } data
			&& data.TryGetProperty("entity_id", out JsonElement entity) && entity.ValueKind == JsonValueKind.String
			&& entity.GetString() is { Length: > 0 } entityId
			? entityId
			: null;

	/// <summary>The words for an automation, named as its entity is called now.</summary>
	public string AutomationWords(string entityId) =>
		$"By automation: {_ha.AttrString(entityId, FriendlyNameAttribute) ?? entityId}";

	/// <summary>The entity id of the automation that ran under this change's context, or its parent's.</summary>
	public string? AutomationEntityId(Context? context)
	{
		if (context is null)
			return null;

		lock (_gate)
		{
			if (context.Id is { Length: > 0 } id && _automationByContext.TryGetValue(id, out string? own))
				return own;

			return context.ParentId is { Length: > 0 } parent && _automationByContext.TryGetValue(parent, out string? caller)
				? caller
				: null;
		}
	}

	private string? PersonName(string? userId)
	{
		if (userId is not { Length: > 0 })
			return null;

		try
		{
			foreach (Entity entity in _ha.GetAllEntities())
			{
				if (entity.EntityId.HasDomain(PersonDomain)
					&& string.Equals(_ha.AttrString(entity.EntityId, UserIdAttribute), userId, StringComparison.Ordinal))
					return _ha.AttrString(entity.EntityId, FriendlyNameAttribute) ?? entity.EntityId;
			}
		}
		catch (InvalidOperationException exception)
		{
			// NetDaemon's state cache throws until its first connection completes.
			_logger.LogDebug(exception, "Could not read the person entities to name a user.");
		}

		return null;
	}

	public void Dispose() => _subscription.Dispose();
}
