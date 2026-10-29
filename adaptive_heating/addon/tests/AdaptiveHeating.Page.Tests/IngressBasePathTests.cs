using AdaptiveLighting.Web.Presentation;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>
/// The guard on the one header the page reads from the browser's side of a proxy. It stops a malformed
/// prefix from breaking every relative link on the settings page, so it is worth proving it can refuse.
/// </summary>
public sealed class IngressBasePathTests
{
	[Fact]
	public void The_trailing_slash_the_header_omits_is_added()
	{
		// Without it the last segment drops off every relative link, which takes every asset with it.
		Assert.Equal("/api/hassio_ingress/abc123/", IngressBasePath.Resolve("/api/hassio_ingress/abc123"));
	}

	[Fact]
	public void A_prefix_that_already_ends_in_a_slash_is_left_alone()
	{
		Assert.Equal("/api/hassio_ingress/abc123/", IngressBasePath.Resolve("/api/hassio_ingress/abc123/"));
	}

	[Fact]
	public void No_header_means_the_plain_site_root()
	{
		// The fallback is what lets the same markup serve a request that never passed through the proxy.
		Assert.Equal("/", IngressBasePath.Resolve(null));
		Assert.Equal("/", IngressBasePath.Resolve(""));
	}

	[Theory]
	[InlineData("api/hassio_ingress/abc")]           // no leading slash
	[InlineData("http://elsewhere/")]                // a scheme
	[InlineData("//elsewhere/path")]                 // a doubled slash
	[InlineData("/api/../../etc")]                   // a parent segment
	[InlineData("/api/..")]                          // a parent segment at the end
	[InlineData("/api\\hassio")]                     // a backslash
	[InlineData("/api/hassio?x=1")]                  // a query
	[InlineData("/api/hassio ingress")]              // whitespace
	[InlineData("/api/\"onload=alert(1)")]           // a quote
	public void A_value_outside_the_shape_renders_the_root_rather_than_failing_the_page(string header)
	{
		Assert.Equal("/", IngressBasePath.Resolve(header));
	}

	[Fact]
	public void A_value_past_the_cap_renders_the_root()
	{
		string tooLong = "/" + new string('a', 256);

		Assert.Equal("/", IngressBasePath.Resolve(tooLong));
	}

	[Fact]
	public void A_value_at_the_cap_is_still_accepted()
	{
		// The boundary in both directions, so the cap cannot drift without a test saying so.
		string atTheCap = "/" + new string('a', 254) + "/";

		Assert.Equal(256, atTheCap.Length);
		Assert.Equal(atTheCap, IngressBasePath.Resolve(atTheCap));
	}
}
