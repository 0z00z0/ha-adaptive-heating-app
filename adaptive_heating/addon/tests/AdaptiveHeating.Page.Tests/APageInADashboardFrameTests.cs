using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

using Adaptive.Settings;

using AdaptiveLighting.Web;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace AdaptiveHeating.Page.Tests;

/// <summary>Lighting's frame rule over heating's document: the framework's refusal stands until an address is listed.</summary>
// The endpoint writes the two headers Blazor and antiforgery write, so the middleware is tested without a render.
public sealed class APageInADashboardFrameTests
{
	private const string FrameworkPolicy = "frame-ancestors 'self'";

	[Fact]
	public async Task With_no_address_the_headers_are_the_frameworks_own()
	{
		using HttpResponseMessage response = await Answer([]);

		Assert.Equal(FrameworkPolicy, Header(response, "Content-Security-Policy"));
		Assert.Equal("SAMEORIGIN", Header(response, "X-Frame-Options"));
	}

	[Fact]
	public async Task A_listed_address_reaches_the_policy_and_takes_x_frame_options_with_it()
	{
		using HttpResponseMessage response = await Answer(["http://10.0.0.5:8123", "not an address"]);

		Assert.Equal("frame-ancestors 'self' http://10.0.0.5:8123", Header(response, "Content-Security-Policy"));
		Assert.Null(Header(response, "X-Frame-Options"));
	}

	private static string? Header(HttpResponseMessage response, string name) =>
		response.Headers.TryGetValues(name, out IEnumerable<string>? values) ? string.Join(", ", values) : null;

	private static async Task<HttpResponseMessage> Answer(IReadOnlyList<string> embedFrom)
	{
		HeatingConfig config = HeatingConfig.CreateDefault();
		config.Global.EmbedFrom = [.. embedFrom];
		AHostedDocument document = AHostedDocument.Holding(HeatingConfigDocument.Serialize(config));
		document.Host.Reload();

		int port = FreePort();
		WebApplicationBuilder builder = WebApplication.CreateBuilder();
		builder.Services.AddSingleton(document.Host);

		await using WebApplication app = builder.Build();
		app.Urls.Add($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}");
		app.UseLightingFrameEmbedding();
		app.MapGet("/", (HttpResponse response) =>
		{
			response.Headers["Content-Security-Policy"] = FrameworkPolicy;
			response.Headers["X-Frame-Options"] = "SAMEORIGIN";

			return "the page";
		});

		await app.StartAsync();

		try
		{
			using HttpClient client = new();

			return await client.GetAsync(new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/"));
		}
		finally
		{
			await app.StopAsync();
		}
	}

	private static int FreePort()
	{
		TcpListener probe = new(IPAddress.Loopback, 0);
		probe.Start();
		int port = ((IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();

		return port;
	}
}
