using NetDaemon.HassModel.Entities;

namespace Adaptive.Engine;

/// <summary>Domain questions on raw entity ids, so a call site never re-declares a <c>"light."</c> prefix constant.</summary>
public static class EntityIdExtensions
{
	/// <summary>Whether the entity id starts with <c>&lt;domain&gt;.</c>, ordinal.</summary>
	public static bool HasDomain(this string entityId, string domain) =>
		entityId.Length > domain.Length
		&& entityId[domain.Length] == '.'
		&& entityId.StartsWith(domain, StringComparison.Ordinal);

	/// <summary>The entity id's domain (the part before the first dot), or <c>null</c> when the id is malformed.</summary>
	public static string? Domain(this string entityId)
	{
		int separator = entityId.IndexOf('.', StringComparison.Ordinal);
		return separator <= 0 ? null : entityId[..separator];
	}

	/// <summary>The entity's domain, or <c>null</c> when its id is malformed.</summary>
	public static string? Domain(this Entity entity) => entity.EntityId.Domain();

	/// <summary>The entity's domain as an <see cref="EntityDomain"/>, or <see cref="EntityDomain.unknown"/> when unrecognised.</summary>
	public static EntityDomain DomainEnum(this Entity entity) =>
		Enum.TryParse<EntityDomain>(entity.EntityId.Split('.').First(), out EntityDomain domain) && Enum.IsDefined(domain)
			? domain
			: EntityDomain.unknown;
}
