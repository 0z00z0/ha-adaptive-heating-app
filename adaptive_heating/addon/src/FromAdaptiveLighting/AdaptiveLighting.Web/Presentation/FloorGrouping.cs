using Adaptive.Engine;

namespace Adaptive.UI;

/// <summary>One floor's rooms, ordered for display. Shared by the dashboard and the Areas section.</summary>
/// <param name="Floor">The floor these items sit on, or <c>null</c> for the trailing floorless group.</param>
/// <param name="Items">The items on it, in the order they were given. The caller has already chosen that order.</param>
public sealed record FloorGroup<T>(AreaFloor? Floor, IReadOnlyList<T> Items);

/// <summary>Groups things that belong to an area by the floor that area sits on.</summary>
/// <remarks>
///     One helper for the dashboard's grid and the Areas list, so one house is never grouped two ways. A house
///     with no floors collapses to a single group with a <c>null</c> floor, which is what lets a renderer show a
///     header only when there is more than one group or the group is named.
/// </remarks>
public static class FloorGrouping
{
	/// <summary>
	///     Groups by floor: ordered by <see cref="AreaFloor.Level"/>, a floor with no level last among floors, then
	///     by name, with the floorless group trailing them all.
	/// </summary>
	/// <param name="items">The things to group. Empty in, empty out.</param>
	/// <param name="areaIdOf">
	///     The area id of one item, or <c>null</c> when it has none. An item with no area id is floorless.
	/// </param>
	/// <param name="registry">Where the floor of an area comes from.</param>
	public static IReadOnlyList<FloorGroup<T>> Group<T>(
		IEnumerable<T> items,
		Func<T, string?> areaIdOf,
		IAreaRegistry registry)
	{
		ArgumentNullException.ThrowIfNull(items);
		ArgumentNullException.ThrowIfNull(areaIdOf);
		ArgumentNullException.ThrowIfNull(registry);

		List<(AreaFloor? Floor, T Item)> placed = [.. items.Select(item => (FloorOf(item, areaIdOf, registry), item))];

		if (placed.Count == 0)
			return [];

		// Keyed by floor id, not by the record: two lookups of one floor are two AreaFloor instances, and the
		// grouping must not depend on the record continuing to compare by value.
		List<FloorGroup<T>> floored = [.. placed
			.Where(entry => entry.Floor is not null)
			.GroupBy(entry => entry.Floor!.Id, StringComparer.Ordinal)
			.Select(group => new FloorGroup<T>(group.First().Floor, [.. group.Select(entry => entry.Item)]))
			.OrderBy(group => group.Floor!.Level ?? int.MaxValue)
			.ThenBy(group => group.Floor!.Name, StringComparer.CurrentCulture)];

		List<T> floorless = [.. placed.Where(entry => entry.Floor is null).Select(entry => entry.Item)];

		if (floorless.Count == 0)
			return floored;

		return [.. floored, new FloorGroup<T>(null, floorless)];
	}

	/// <summary><see cref="Group"/>, degrading to one unnamed group when the registry cannot answer.</summary>
	/// <remarks>
	///     A registry that has not connected throws instead of answering. The catch lives here so no screen can
	///     forget it and take the page down over a dropped connection.
	/// </remarks>
	/// <param name="registry">Where the floor of an area comes from. May be unreachable.</param>
	/// <returns>The groups, or one unnamed group holding everything when the floors cannot be read.</returns>
	public static IReadOnlyList<FloorGroup<T>> GroupOrFlat<T>(
		IEnumerable<T> items,
		Func<T, string?> areaIdOf,
		IAreaRegistry registry)
	{
		ArgumentNullException.ThrowIfNull(items);

		try
		{
			return Group(items, areaIdOf, registry);
		}
		catch (InvalidOperationException)
		{
			List<T> all = [.. items];
			return all.Count == 0 ? [] : [new FloorGroup<T>(null, all)];
		}
	}

	private static AreaFloor? FloorOf<T>(T item, Func<T, string?> areaIdOf, IAreaRegistry registry) =>
		areaIdOf(item) is { Length: > 0 } areaId ? registry.FloorOf(areaId) : null;
}
