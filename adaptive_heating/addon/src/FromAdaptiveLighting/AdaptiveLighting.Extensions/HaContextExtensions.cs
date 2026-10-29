using NetDaemon.HassModel.Entities;

namespace Adaptive.Engine;

/// <summary>Verbs and one-hop questions on <see cref="IHaContext"/>, with the domain derived from the entity id.</summary>
public static class HaContextExtensions
{
	// ---- questions ----------------------------------------------------------------------

	/// <summary>Whether the entity currently reads on; <c>false</c> when it is unknown or unavailable.</summary>
	public static bool IsOn(this IHaContext ha, string entityId) => ha.GetState(entityId)?.IsOn() ?? false;

	/// <summary>Whether the entity currently reads off; <c>false</c> when it is unknown or unavailable.</summary>
	/// <remarks><see cref="IsOff"/> is never <c>!IsOn</c>: both are false for an unavailable entity.</remarks>
	public static bool IsOff(this IHaContext ha, string entityId) => ha.GetState(entityId)?.IsOff() ?? false;

	/// <summary>Whether the entity's state equals <paramref name="value"/>, ordinal-ignore-case.</summary>
	public static bool StateIs(this IHaContext ha, string entityId, string value) => ha.GetState(entityId).StateIs(value);

	/// <summary>Reads a numeric attribute off the entity, or <c>null</c> when absent or not a number.</summary>
	public static double? AttrDouble(this IHaContext ha, string entityId, string attribute) => ha.GetState(entityId).AttrDouble(attribute);

	/// <summary>Reads a string attribute off the entity, or <c>null</c> when absent.</summary>
	public static string? AttrString(this IHaContext ha, string entityId, string attribute) => ha.GetState(entityId).AttrString(attribute);

	/// <summary>Reads a string-list attribute off the entity, or an empty list when absent or not a list.</summary>
	public static IReadOnlyList<string> AttrStringList(this IHaContext ha, string entityId, string attribute) => ha.GetState(entityId).AttrStringList(attribute);

	/// <summary>Every entity id in <paramref name="domain"/>, ordered ordinally.</summary>
	public static IReadOnlyList<string> EntityIdsInDomain(this IHaContext ha, string domain) =>
		[.. ha.GetAllEntities()
			.Select(entity => entity.EntityId)
			.Where(id => id.HasDomain(domain))
			.Order(StringComparer.Ordinal)];

	// ---- verbs --------------------------------------------------------------------------

	/// <summary>Turns one or more entities on; the domain is inferred per id, and a mixed set uses <c>homeassistant</c>.</summary>
	public static void TurnOn(this IHaContext ha, params string[] entityIds)
	{
		if (entityIds.Length == 0)
			throw new ArgumentNullException(nameof(entityIds));

		ha.CallService(DomainForServiceCall(entityIds), "turn_on", new ServiceTarget { EntityIds = entityIds });
	}

	/// <summary>Turns one or more entities off; the domain is inferred per id, and a mixed set uses <c>homeassistant</c>.</summary>
	public static void TurnOff(this IHaContext ha, params string[] entityIds)
	{
		if (entityIds.Length == 0)
			throw new ArgumentNullException(nameof(entityIds));

		ha.CallService(DomainForServiceCall(entityIds), "turn_off", new ServiceTarget { EntityIds = entityIds });
	}

	/// <summary>Turns an entity on, passing <paramref name="data"/> (e.g. <c>brightness_pct</c>) with the call.</summary>
	public static void TurnOn(this IHaContext ha, string entityId, object data)
	{
		string[] entityIds = new[] { entityId };
		ha.CallService(DomainForServiceCall(entityIds), "turn_on", new ServiceTarget { EntityIds = entityIds }, data);
	}

	/// <summary>Turns an entity off, passing <paramref name="data"/> with the call.</summary>
	public static void TurnOff(this IHaContext ha, string entityId, object data)
	{
		string[] entityIds = new[] { entityId };
		ha.CallService(DomainForServiceCall(entityIds), "turn_off", new ServiceTarget { EntityIds = entityIds }, data);
	}

	/// <summary>Calls a service named as one <c>domain.service</c> id, splitting it into domain and service.</summary>
	/// <remarks>Named apart from <c>CallService</c> so overload resolution cannot surprise.</remarks>
	/// <exception cref="ArgumentException"><paramref name="fullServiceId"/> is not a <c>domain.service</c> id.</exception>
	public static void CallServiceById(this IHaContext ha, string fullServiceId, ServiceTarget? target = null, object? data = null)
	{
		string[]? parts = fullServiceId?.Split('.', 2);
		if (parts is not { Length: 2 } || parts.Any(string.IsNullOrWhiteSpace))
			throw new ArgumentException($"'{fullServiceId}' is not a 'domain.service' id.", nameof(fullServiceId));

		ha.CallService(parts[0], parts[1], target, data);
	}

	/// <summary>Raises a persistent notification via <c>persistent_notification.create</c>.</summary>
	/// <remarks>
	///     Only <c>persistent_notification.create</c> takes an id; <c>notify.persistent_notification</c> does not. With
	///     an id, re-raising replaces the card instead of stacking one.
	/// </remarks>
	public static void NotifyPersistent(this IHaContext ha, string title, string message, string? notificationId = null)
	{
		Dictionary<string, object> data = new(StringComparer.Ordinal)
		{
			["title"] = title,
			["message"] = message
		};

		if (notificationId is { Length: > 0 })
			data["notification_id"] = notificationId;

		ha.CallService("persistent_notification", "create", data: data);
	}

	/// <summary>Sets an <c>input_text</c> helper's value.</summary>
	public static void SetInputText(this IHaContext ha, string entityId, string value) =>
		ha.CallService("input_text", "set_value", ServiceTarget.FromEntity(entityId), new { value });

	/// <summary>Sets an <c>input_number</c> helper's value.</summary>
	public static void SetInputNumber(this IHaContext ha, string entityId, double value) =>
		ha.CallService("input_number", "set_value", ServiceTarget.FromEntity(entityId), new { value });

	private static string DomainForServiceCall(string[] entityIds)
	{
		// A malformed id has no domain to call under, so it fails loud instead of reaching CallService as null.
		List<string> domains = entityIds
			.Select(id => id.Domain() ?? throw new ArgumentException($"'{id}' is not a valid entity id.", nameof(entityIds)))
			.ToList();

		return domains.Distinct(StringComparer.Ordinal).Count() == 1 ? domains[0] : "homeassistant";
	}
}
