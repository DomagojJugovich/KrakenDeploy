using System.Net;
using System.Net.Sockets;

namespace KrakenDeploy.Server.Data.Net;

/// <summary>
/// Builds a <see cref="SocketsHttpHandler"/> whose <see cref="SocketsHttpHandler.ConnectCallback"/>
/// enforces an <see cref="SsrfPolicy"/> on <b>every</b> connection the handler opens —
/// the initial request and each redirect hop. For each connection it resolves the
/// target host, validates every candidate address against the policy, and connects
/// to (pins) a validated IP. Because the socket is opened directly to the vetted
/// address rather than re-resolving the name, this closes the DNS-rebind TOCTOU that
/// a pre-flight-only check leaves open, and — by running per connection — it also
/// closes redirect-based SSRF bypasses.
/// <para>
/// TLS is still negotiated by the handler on top of the returned stream using the
/// request's hostname, so certificate/SNI validation is unaffected.
/// </para>
/// </summary>
public static class SsrfHttpHandlerFactory
{
    /// <summary>
    /// Creates an SSRF-enforcing primary handler.
    /// </summary>
    /// <param name="policy">The per-integration policy to enforce on every hop.</param>
    /// <param name="allowAutoRedirect">
    /// Whether the handler follows 3xx redirects. Pass <c>false</c> for integrations
    /// that should never redirect (webhook delivery); pass <c>true</c> where redirects
    /// are legitimate (GitHub release-asset downloads 302 to a CDN) — the connect
    /// callback re-validates the redirect target either way.
    /// </param>
    public static SocketsHttpHandler Create(SsrfPolicy policy, bool allowAutoRedirect)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = allowAutoRedirect,
            ConnectCallback = (context, ct) => ConnectAsync(policy, context, ct),
        };
        return handler;
    }

    private static async ValueTask<Stream> ConnectAsync(
        SsrfPolicy policy, SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            if (addresses.Length == 0)
            {
                throw new HttpRequestException(
                    $"SSRF guard: host '{host}' did not resolve to any IP address.");
            }
        }

        Exception? lastConnectError = null;
        var anyAllowed = false;

        foreach (var address in addresses)
        {
            if (SsrfGuard.EvaluateAddress(address, host, policy) is not null)
            {
                continue; // refused by policy — try the next candidate
            }

            anyAllowed = true;
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            try
            {
                // Connect to the exact vetted address — the name is not re-resolved,
                // so the connected IP is provably the one the policy approved.
                await socket.ConnectAsync(new IPEndPoint(address, port), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                socket.Dispose();
                lastConnectError = ex;
            }
        }

        if (!anyAllowed)
        {
            throw new HttpRequestException(
                $"SSRF guard: no allowed address for host '{host}' " +
                "(all candidates are blocked by policy).");
        }

        throw new HttpRequestException(
            $"SSRF guard: could not connect to any allowed address for host '{host}'.",
            lastConnectError);
    }
}
