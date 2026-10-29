using Microsoft.AspNetCore.Http;

namespace AdaptiveLighting.Web.Presentation;

/// <summary>The address Lamplight's pages declare as their base, taken from Home Assistant's ingress header.</summary>
/// <remarks>Home Assistant strips the per-installation prefix before the request arrives, so routing is
/// unchanged and only the base address the browser resolves against has to move. The header, the validation
/// rule and what lives outside this repository are in docs/mechanisms.md, "Serving Lamplight behind Home
/// Assistant's ingress".</remarks>
public static class IngressBasePath
{
	/// <summary>The header Home Assistant Core adds when it proxies a request through ingress.</summary>
	public const string HeaderName = "X-Ingress-Path";

	/// <summary>What a page declares when no ingress prefix applies.</summary>
	public const string Root = "/";

	// Home Assistant's own prefix is "/api/hassio_ingress/<token>", around 60 characters. The cap is a ceiling
	// on what a client can push into the page, not a measurement of a real prefix.
	private const int MaxLength = 256;

	/// <summary>The base address for this request: the ingress prefix with a trailing slash, or <see cref="Root"/>.</summary>
	public static string For(HttpContext? context) =>
		context is null ? Root : Resolve(context.Request.Headers[HeaderName]);

	/// <summary>The base address a header value asks for, or <see cref="Root"/> when it asks for nothing safe.</summary>
	public static string Resolve(string? header)
	{
		if (header is not { Length: > 0 and <= MaxLength } prefix)
			return Root;

		if (prefix[0] != '/' || prefix.Contains("//", StringComparison.Ordinal))
			return Root;

		foreach (char character in prefix)
		{
			// An allow-list, so a scheme, a backslash, a query, a quote, whitespace and every control character
			// fail in one place instead of one rule each.
			if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.' or '~' or '/'))
				return Root;
		}

		// Only sound because the leading '/' is already checked above: every segment is slash-delimited on both
		// sides, or is the last one.
		if (prefix.Contains("/../", StringComparison.Ordinal) || prefix.EndsWith("/..", StringComparison.Ordinal))
			return Root;

		return prefix.EndsWith('/') ? prefix : prefix + "/";
	}
}
