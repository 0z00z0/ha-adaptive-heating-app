namespace AdaptiveLighting.Configuration;

/// <summary>The label settings that hold no label id the house knows.</summary>
public static class LabelTranslation
{
	/// <summary>The label settings that are set but are not ids the house knows, by their setting name.</summary>
	/// <remarks>What the validator warns about: a label is matched by id only, so each of these matches nothing.</remarks>
	public static IEnumerable<(string Setting, string Value)> NotIds(GlobalConfig global, IReadOnlyCollection<string>? knownLabelIds)
	{
		ArgumentNullException.ThrowIfNull(global);

		if (knownLabelIds is null)
			yield break;

		foreach ((string setting, string? value) in Settings(global))
			if (value is { Length: > 0 } && !knownLabelIds.Contains(value, StringComparer.Ordinal))
				yield return (setting, value);
	}

	private static IEnumerable<(string Setting, string? Value)> Settings(GlobalConfig global)
	{
		yield return (nameof(GlobalConfig.ExcludeLabel), global.ExcludeLabel);
		yield return (nameof(GlobalConfig.IncludeLabel), global.IncludeLabel);
	}
}
