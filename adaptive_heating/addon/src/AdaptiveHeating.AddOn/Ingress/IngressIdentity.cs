using System.Security.Claims;

using Microsoft.AspNetCore.Http;

namespace AdaptiveHeating.AddOn.Ingress;

/// <summary>The three headers Home Assistant's ingress names the signed-in person in.</summary>
/// <remarks>
///     Read only after the socket check has passed. Reading them first lets anyone who can reach an open port
///     claim to be anybody. The gateway strips any incoming copies of them, which is what makes them worth
///     anything on the ingress listener and worth nothing anywhere else.
/// </remarks>
public static class IngressIdentity
{
	public const string UserIdHeader = "X-Remote-User-Id";

	public const string UserNameHeader = "X-Remote-User-Name";

	public const string DisplayNameHeader = "X-Remote-User-Display-Name";

	/// <summary>The scheme every page and every endpoint asks, whichever way the request came in.</summary>
	public const string Scheme = "HomeAssistant";

	/// <summary>Who the headers name, or <c>null</c> where they name nobody.</summary>
	/// <remarks>Only the Home Assistant provider is documented to carry a user name, so a person signed in
	/// another way falls back to their display name and then to their identifier. The session is never
	/// nameless.</remarks>
	public static ClaimsPrincipal? Read(HttpRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);

		string? id = Single(request, UserIdHeader);

		if (id is null)
			return null;

		string name = Single(request, UserNameHeader) ?? Single(request, DisplayNameHeader) ?? id;
		string display = Single(request, DisplayNameHeader) ?? name;

		ClaimsIdentity identity = new(
			[
				new Claim(ClaimTypes.NameIdentifier, id),
				new Claim(ClaimTypes.Name, name),
				new Claim("display_name", display)
			],
			Scheme,
			ClaimTypes.Name,
			ClaimTypes.Role);

		return new ClaimsPrincipal(identity);
	}

	// A header the gateway sent once. More than one copy is a caller pushing values in, so it names nobody.
	private static string? Single(HttpRequest request, string header) =>
		request.Headers[header] is [{ Length: > 0 } only] ? only : null;
}
