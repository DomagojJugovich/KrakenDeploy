using System.Net;
using System.Net.Sockets;

namespace KrakenDeploy.Server.Data.Net;

/// <summary>
/// Guards outbound, operator-supplied URLs (webhooks, AI endpoints) against the
/// narrow SSRF vectors that matter for an on-prem install: the loopback
/// interface and the link-local / cloud-metadata range.
/// <para>
/// Deliberately NOT a full private-range block: gov on-prem networks legitimately
/// run webhook receivers on RFC1918 internal hosts (10.x, 172.16-31.x, 192.168.x),
/// so blocking those would break real integrations. We block only:
/// </para>
/// <list type="bullet">
///   <item>Loopback — IPv4 127.0.0.0/8 and IPv6 ::1.</item>
///   <item>Link-local / metadata — IPv4 169.254.0.0/16 (covers the
///         169.254.169.254 cloud-metadata endpoint) and IPv6 fe80::/10.</item>
///   <item>The unspecified address (0.0.0.0 / ::) — never a valid destination and
///         routes locally on most stacks.</item>
/// </list>
/// <para>
/// The host is DNS-resolved and EVERY returned address is checked, so a name
/// that points at loopback is caught. A determined attacker can still DNS-rebind
/// between this check and the connection (TOCTOU) — closing that needs a custom
/// connect callback that pins the validated IP; out of scope for this guard,
/// which targets the common misconfiguration / metadata-exfil case.
/// </para>
/// </summary>
public static class SsrfGuard
{
    /// <summary>
    /// Returns <c>null</c> when <paramref name="url"/> is allowed, or a
    /// human-readable reason when it must be refused.
    /// </summary>
    public static async Task<string?> ValidateOutboundUrlAsync(
        string? url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "URL is not a valid absolute URI.";
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return $"URL scheme '{uri.Scheme}' is not allowed (only http/https).";
        }

        IReadOnlyList<IPAddress> addresses;
        if (IPAddress.TryParse(uri.Host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                var resolved = await Dns.GetHostAddressesAsync(uri.Host, ct).ConfigureAwait(false);
                if (resolved.Length == 0)
                {
                    return $"Host '{uri.Host}' did not resolve to any IP address.";
                }
                addresses = resolved;
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException)
            {
                return $"Could not resolve host '{uri.Host}': {ex.Message}";
            }
        }

        foreach (var address in addresses)
        {
            if (IsBlocked(address))
            {
                return $"URL host '{uri.Host}' resolves to a blocked address ({address}); " +
                       "loopback and link-local/metadata addresses are not allowed.";
            }
        }

        return null;
    }

    /// <summary>True when <paramref name="address"/> is loopback, link-local/metadata, or unspecified.</summary>
    public static bool IsBlocked(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        // Normalise IPv4-mapped IPv6 (e.g. ::ffff:127.0.0.1) to its IPv4 form so
        // the byte checks below catch a mapped loopback / link-local too.
        var addr = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        // Loopback: IPv4 127.0.0.0/8 and IPv6 ::1.
        if (IPAddress.IsLoopback(addr))
        {
            return true;
        }

        // Unspecified (0.0.0.0 / ::) — never a real destination; routes locally.
        if (addr.Equals(IPAddress.Any) || addr.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        if (addr.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = addr.GetAddressBytes();
            // 169.254.0.0/16 link-local (includes 169.254.169.254 metadata).
            if (b[0] == 169 && b[1] == 254)
            {
                return true;
            }
        }
        else if (addr.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // fe80::/10 link-local.
            if (addr.IsIPv6LinkLocal)
            {
                return true;
            }
        }

        return false;
    }
}
