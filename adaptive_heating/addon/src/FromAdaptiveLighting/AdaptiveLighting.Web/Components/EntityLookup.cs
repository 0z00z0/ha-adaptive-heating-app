namespace Adaptive.UI;

/// <summary>What a picker needs to know about an id it did not offer: what to call it, and whether it exists.</summary>
/// <remarks>
///     The two answers travel together because half a pair is the failure they had: a control given a name
///     resolver and no known resolver reads every configured value as unknown, and the other way round it
///     reads a typo as a real entity.
/// </remarks>
public sealed record EntityLookup(Func<string, string> Name, Func<string, bool> Knows)
{
	/// <summary>A lookup that knows nothing: an id is its own name, and every id is taken on trust.</summary>
	public static readonly EntityLookup Ids = new(entityId => entityId, _ => true);
}
