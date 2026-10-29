using System.Security.Claims;

using AdaptiveHeating.AddOn.Ingress;

using Microsoft.AspNetCore.Http;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>
/// The socket rule and the identity headers. A header is something a caller writes and a socket is not, so
/// the check that tells one way in from the other is the one thing here that must not be got wrong.
/// </summary>
public sealed class IngressIdentityTests
{
	private static readonly WayIn OnPort8099 = new(8099);

	[Fact]
	public void A_request_on_the_ingress_port_is_the_ingress_way_in()
	{
		Assert.True(OnPort8099.IsIngress(Arriving(on: 8099)));
	}

	[Fact]
	public void A_request_on_any_other_port_is_not()
	{
		// The control: without this failing, the check above proves nothing about what it refuses.
		Assert.False(OnPort8099.IsIngress(Arriving(on: 8098)));
		Assert.False(OnPort8099.IsIngress(Arriving(on: 80)));
	}

	[Fact]
	public void The_headers_name_the_person_they_carry()
	{
		HttpContext context = Arriving(on: 8099);
		context.Request.Headers["X-Remote-User-Id"] = "abc123";
		context.Request.Headers["X-Remote-User-Name"] = "alex";
		context.Request.Headers["X-Remote-User-Display-Name"] = "Alex";

		ClaimsPrincipal? who = IngressIdentity.Read(context.Request);

		Assert.NotNull(who);
		Assert.Equal("abc123", who.FindFirst(ClaimTypes.NameIdentifier)?.Value);
		Assert.Equal("alex", who.Identity?.Name);
		Assert.Equal("Alex", SignedIn.DisplayName(who));
	}

	[Fact]
	public void A_person_with_no_user_name_is_never_nameless()
	{
		// Only Home Assistant's own provider is documented to carry a user name.
		HttpContext context = Arriving(on: 8099);
		context.Request.Headers["X-Remote-User-Id"] = "abc123";
		context.Request.Headers["X-Remote-User-Display-Name"] = "Alex";

		ClaimsPrincipal? who = IngressIdentity.Read(context.Request);

		Assert.Equal("Alex", who?.Identity?.Name);
	}

	[Fact]
	public void No_identifier_names_nobody()
	{
		HttpContext context = Arriving(on: 8099);
		context.Request.Headers["X-Remote-User-Name"] = "alex";

		Assert.Null(IngressIdentity.Read(context.Request));
	}

	[Fact]
	public void A_duplicated_header_names_nobody()
	{
		// Two copies is a caller pushing values in, not the gateway, which sets exactly one.
		HttpContext context = Arriving(on: 8099);
		context.Request.Headers["X-Remote-User-Id"] = new[] { "abc123", "someone-else" };

		Assert.Null(IngressIdentity.Read(context.Request));
	}

	private static HttpContext Arriving(int on)
	{
		DefaultHttpContext context = new();
		context.Connection.LocalPort = on;

		return context;
	}
}
