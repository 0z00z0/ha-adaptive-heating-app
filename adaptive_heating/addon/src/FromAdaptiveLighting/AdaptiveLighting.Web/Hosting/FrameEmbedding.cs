using AdaptiveLighting.Configuration;
using Adaptive.Settings;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AdaptiveLighting.Web;

/// <summary>Lets the addresses in <see cref="GlobalConfig.EmbedFrom"/> show these pages inside a frame.</summary>
/// <remarks>
///     Two framework headers stand in the way and neither is set anywhere in this repository. Blazor's
///     interactive server endpoint writes <c>Content-Security-Policy: frame-ancestors 'self'</c>, from
///     <c>ServerComponentsEndpointOptions.ContentSecurityFrameAncestorsPolicy</c>; ASP.NET Core's antiforgery
///     writes <c>X-Frame-Options: SAMEORIGIN</c> beside it. Both are start-up options, so neither can follow a
///     document that is edited while the house runs.
/// </remarks>
public static class FrameEmbedding
{
	private const string ContentSecurityPolicy = "Content-Security-Policy";
	private const string XFrameOptions = "X-Frame-Options";

	/// <summary>Rewrites those two headers on the way out, and only while the document names an address.</summary>
	/// <remarks>
	///     Install before the endpoints; one pipeline covers every port the process listens on, so both designs
	///     get the same treatment from this one call.
	/// </remarks>
	public static IApplicationBuilder UseLightingFrameEmbedding(this IApplicationBuilder app)
	{
		ArgumentNullException.ThrowIfNull(app);

		// The host's parsed document: a save replaces it, so an edited list applies to the next response.
		HeatingDocumentHost document = app.ApplicationServices.GetRequiredService<HeatingDocumentHost>();

		app.Use((context, next) =>
		{
			context.Response.OnStarting(static state => Relax((HeaderState)state), new HeaderState(context.Response, document));

			return next(context);
		});

		return app;
	}

	/// <summary>The headers as the browser will see them, decided after the endpoint has written its own.</summary>
	private static Task Relax(HeaderState state)
	{
		IHeaderDictionary headers = state.Response.Headers;

		// Nothing to relax on a static asset or a 404: neither framework header is on those responses at all,
		// and touching them would be a change where the default is meant to be untouched.
		if (!headers.ContainsKey(ContentSecurityPolicy) && !headers.ContainsKey(XFrameOptions))
			return Task.CompletedTask;

		if (Origins(state.Document) is not { Count: > 0 } origins)
			return Task.CompletedTask;

		headers[ContentSecurityPolicy] = $"frame-ancestors 'self' {string.Join(' ', origins)}";

		// X-Frame-Options holds one source and no cross-origin one, so a browser that reads it refuses the frame
		// however permissive the policy beside it is. Removing it is what leaves the policy in charge.
		headers.Remove(XFrameOptions);

		return Task.CompletedTask;
	}

	private static IReadOnlyList<string> Origins(HeatingDocumentHost document) =>
		EmbedOrigin.ReadAll(document.Current.Global.EmbedFrom);

	private sealed record HeaderState(HttpResponse Response, HeatingDocumentHost Document);
}
