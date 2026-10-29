using System.Globalization;

using Microsoft.AspNetCore.Http;

namespace AdaptiveHeating.AddOn.Ingress;

/// <summary>
/// Which listener a request arrived on. The page tells one way in from the other by the socket, never by a
/// header: a header is something a caller writes and a socket is not.
/// </summary>
/// <remarks>
///     One listener exists today, the one Home Assistant's ingress reaches inside the container. A second way
///     in is a second port and a second entry here; nothing else on the page changes, because every page and
///     every endpoint asks the principal and none of them asks which way the request came in.
/// </remarks>
public sealed class WayIn
{
	/// <summary>The port Home Assistant's ingress reaches inside the container, named in the manifest.</summary>
	public const int DefaultIngressPort = 8099;

	public WayIn(int ingressPort)
	{
		if (ingressPort is <= 0 or > 65535)
			throw new ArgumentOutOfRangeException(nameof(ingressPort), ingressPort, "A port is 1 to 65535.");

		IngressPort = ingressPort;
	}

	public int IngressPort { get; }

	/// <summary>Reads the configured port, falling back to the one the manifest names.</summary>
	public static int PortFrom(string? configured) =>
		int.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out int port) && port is > 0 and <= 65535
			? port
			: DefaultIngressPort;

	/// <summary>True where this request landed on the ingress listener.</summary>
	public bool IsIngress(HttpContext context)
	{
		ArgumentNullException.ThrowIfNull(context);

		return context.Connection.LocalPort == IngressPort;
	}
}
