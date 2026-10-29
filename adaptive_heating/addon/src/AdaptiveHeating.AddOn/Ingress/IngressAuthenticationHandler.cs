using System.Security.Claims;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AdaptiveHeating.AddOn.Ingress;

/// <summary>
/// Resolves who is signed in, once per request, behind one scheme. Every page and every endpoint asks this
/// principal, and none of them asks which way the request came in.
/// </summary>
public sealed class IngressAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
	private readonly WayIn _wayIn;

	public IngressAuthenticationHandler(
		IOptionsMonitor<AuthenticationSchemeOptions> options,
		ILoggerFactory logger,
		UrlEncoder encoder,
		WayIn wayIn)
		: base(options, logger, encoder) =>
		_wayIn = wayIn ?? throw new ArgumentNullException(nameof(wayIn));

	/// <inheritdoc/>
	protected override Task<AuthenticateResult> HandleAuthenticateAsync()
	{
		// The socket first, always. The identity headers are worth nothing on any other listener, because
		// only Home Assistant's gateway strips incoming copies of them before it sets its own.
		if (!_wayIn.IsIngress(Context))
			return Task.FromResult(AuthenticateResult.NoResult());

		if (IngressIdentity.Read(Context.Request) is not { } principal)
			return Task.FromResult(AuthenticateResult.NoResult());

		return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
	}
}

/// <summary>What the page knows about the person reading it.</summary>
public static class SignedIn
{
	/// <summary>The display name, or <c>null</c> where nobody was named.</summary>
	public static string? DisplayName(ClaimsPrincipal? principal) =>
		principal?.FindFirst("display_name")?.Value;
}
