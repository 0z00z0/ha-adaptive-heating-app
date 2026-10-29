using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

namespace Adaptive.UI;

/// <summary>One room's line in the re-setup warning: what it is called and what the rebuild costs it.</summary>
public sealed record SetupWarningLine(string Name, string Consequence, string? Note);

/// <summary>Turns a <see cref="SetupPlan"/> into the words the re-setup warning shows.</summary>
/// <remarks>
///     The three things counted are the three <c>AreaSetupService.Apply</c> destroys, and they are read off the
///     plan, never off the document, so the warning cannot describe a different rebuild from the one about to happen.
/// </remarks>
public static class SetupWarning
{
	/// <summary>The dialog's question, sized to what the plan does.</summary>
	public static string Title(SetupPlan plan)
	{
		ArgumentNullException.ThrowIfNull(plan);

		if (plan.Rebuilds.Count > 0)
			return $"Set up {Count(plan.Rebuilds.Count, "room")} again?";

		return plan.NewAreas.Count > 0
			? $"Add {Count(plan.NewAreas.Count, "new room")}?"
			: "Nothing to set up";
	}

	/// <summary>One line per row the plan rebuilds, in the plan's own order.</summary>
	public static IReadOnlyList<SetupWarningLine> Lines(SetupPlan plan, HeatingConfig config)
	{
		ArgumentNullException.ThrowIfNull(plan);
		ArgumentNullException.ThrowIfNull(config);

		List<SetupWarningLine> lines = [];

		// A document can carry two rows for one area id, and Plan emits one rebuild per row. Rows are taken by
		// position among those sharing an id, never by first match, or the second rebuild would be warned about
		// under the first row's custom name.
		ILookup<string, AreaConfig> rows = config.Areas
			.Where(candidate => candidate.AreaId is { Length: > 0 })
			.ToLookup(candidate => candidate.AreaId!, StringComparer.Ordinal);

		Dictionary<string, int> taken = new(StringComparer.Ordinal);

		foreach (AreaRebuildPlan rebuild in plan.Rebuilds)
		{
			int position = taken.GetValueOrDefault(rebuild.AreaId);
			taken[rebuild.AreaId] = position + 1;

			AreaConfig? area = rows[rebuild.AreaId].ElementAtOrDefault(position);

			lines.Add(new SetupWarningLine(
				area?.DisplayName ?? rebuild.AreaId,
				Consequence(rebuild, area?.Name),
				rebuild.SkipReason is { Length: > 0 } reason ? $"It {reason}" : null));
		}

		return lines;
	}

	/// <summary>The sentence about rooms the run adds, or <c>null</c> when it adds none.</summary>
	public static string? NewRooms(SetupPlan plan, Func<string, string>? nameOf)
	{
		ArgumentNullException.ThrowIfNull(plan);

		if (plan.NewAreas.Count == 0)
			return null;

		IEnumerable<string> names = plan.NewAreas
			.Select(area => area.AreaId ?? string.Empty)
			.Where(areaId => areaId.Length > 0)
			.Select(areaId => nameOf?.Invoke(areaId) ?? areaId);

		return $"{Count(plan.NewAreas.Count, "new room")} will be added, switched off: {string.Join(", ", names)}.";
	}

	private static string Consequence(AreaRebuildPlan rebuild, string? customName)
	{
		List<string> losses = [];

		if (rebuild.HasCustomName)
		{
			losses.Add(customName is { Length: > 0 } name
				? $"its custom name (“{name}”)"
				: "its custom name");
		}

		if (rebuild.PinnedEntityCount > 0)
			losses.Add($"{Count(rebuild.PinnedEntityCount, "hand-picked entity", "hand-picked entities")}");

		if (rebuild.OverrideCount > 0)
			losses.Add($"{Count(rebuild.OverrideCount, "changed setting")}");

		return losses.Count == 0
			? "nothing to lose"
			: $"loses {Join(losses)}";
	}

	/// <summary>"a", "a and b", "a, b and c".</summary>
	private static string Join(IReadOnlyList<string> parts) => parts.Count switch
	{
		1 => parts[0],
		2 => $"{parts[0]} and {parts[1]}",
		_ => $"{string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]}"
	};

	private static string Count(int count, string singular, string? plural = null) =>
		$"{count} {(count == 1 ? singular : plural ?? singular + "s")}";
}
