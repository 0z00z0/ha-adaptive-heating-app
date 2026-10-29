namespace AdaptiveLighting.Configuration;

/// <summary>Reads one <see cref="GlobalConfig.EmbedFrom"/> entry as the origin a browser compares a frame's parent against.</summary>
/// <remarks>
///     The one reader, so the settings page's warning and the header actually sent can never disagree about what
///     counts as an address.
/// </remarks>
public static class EmbedOrigin
{
	/// <summary>Whether <paramref name="value"/> is an address, and the scheme, host and port a header would carry.</summary>
	/// <remarks>Anything after the port is dropped: a frame's parent is matched by origin, so a path would never be read.</remarks>
	public static bool TryRead(string? value, out string origin)
	{
		origin = string.Empty;

		if (value is not { Length: > 0 } text || string.IsNullOrWhiteSpace(text))
			return false;

		if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri? address))
			return false;

		if (address.Scheme is not ("http" or "https") || address.Host.Length == 0)
			return false;

		origin = address.GetLeftPart(UriPartial.Authority);

		return origin.Length > 0;
	}

	/// <summary>Every entry that reads as an address, in the order given, with duplicates dropped.</summary>
	public static IReadOnlyList<string> ReadAll(IEnumerable<string>? values)
	{
		if (values is null)
			return [];

		List<string> origins = [];

		foreach (string value in values)
			if (TryRead(value, out string origin) && !origins.Contains(origin, StringComparer.OrdinalIgnoreCase))
				origins.Add(origin);

		return origins;
	}
}
