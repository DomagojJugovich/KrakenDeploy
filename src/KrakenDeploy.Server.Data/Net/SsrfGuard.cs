using System.Net;
using System.Net.Sockets;

namespace KrakenDeploy.Server.Data.Net;

/// <summary>
/// Guards outbound, operator-supplied URLs (webhooks, catalog, OIDC, AI endpoints)
/// against SSRF. Enforcement has two layers that share this classification:
/// <list type="number">
///   <item>A pre-flight <see cref="ValidateOutboundUrlAsync"/> check for a clean,
///         early failure with a human-readable reason.</item>
///   <item>A per-connection <see cref="SsrfHttpHandlerFactory"/> connect callback
///         that re-validates and <b>pins</b> the resolved IP on every hop, closing
///         both the redirect bypass and the DNS-rebind TOCTOU.</item>
/// </list>
/// <para>
/// Two tiers of address are treated differently:
/// </para>
/// <list type="bullet">
///   <item><b>Hard-blocked (never allowlistable)</b> — link-local / cloud-metadata
///         (IPv4 169.254.0.0/16 incl. 169.254.169.254, IPv6 fe80::/10) and the
///         unspecified address (0.0.0.0 / ::). Re-enabling these would defeat the
///         guard's purpose.</item>
///   <item><b>Policy-gated</b> — loopback (127.0.0.0/8, ::1) and private ranges
///         (RFC1918, CGNAT 100.64/10, IPv6 ULA fc00::/7). Denied by default; an
///         operator opts in per integration via <see cref="SsrfPolicy"/>.</item>
/// </list>
/// </summary>
public static class SsrfGuard
{
    /// <summary>
    /// Pre-flight check. Returns <c>null</c> when <paramref name="url"/> is allowed
    /// under <paramref name="policy"/>, or a human-readable reason when it must be
    /// refused. DNS is resolved and every returned address is checked; if any
    /// resolves to a refused address the whole URL is refused (conservative).
    /// </summary>
    public static async Task<string?> ValidateOutboundUrlAsync(
        string? url, SsrfPolicy policy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "URL is not a valid absolute URI.";
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return $"URL scheme '{uri.Scheme}' is not allowed (only http/https).";
        }

        IPAddress[] addresses;
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
            var reason = EvaluateAddress(address, uri.Host, policy);
            if (reason is not null)
            {
                return reason;
            }
        }

        return null;
    }

    /// <summary>
    /// Classifies a single resolved <paramref name="address"/> for
    /// <paramref name="host"/> under <paramref name="policy"/>. Returns <c>null</c>
    /// when the address may be contacted, or a refusal reason otherwise.
    /// Evaluation order: hard-block (unconditional) → allowlist → loopback flag →
    /// private flag → allow.
    /// </summary>
    public static string? EvaluateAddress(IPAddress address, string host, SsrfPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(policy);

        // Normalise IPv4-mapped IPv6 (e.g. ::ffff:127.0.0.1) so the byte checks
        // below catch a mapped loopback / link-local too.
        var addr = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        // Tier 1 — hard block, never allowlistable.
        if (IsHardBlocked(addr))
        {
            return $"URL host '{host}' resolves to a blocked address ({addr}); " +
                   "link-local/metadata and unspecified addresses are never allowed.";
        }

        // Tier 2 — explicit operator allowlist bypasses the policy-gated denials.
        if (policy.MatchesHost(host) || policy.MatchesAddress(addr))
        {
            return null;
        }

        if (IsLoopback(addr) && !policy.AllowLoopback)
        {
            return $"URL host '{host}' resolves to a loopback address ({addr}); " +
                   "loopback is denied for this integration (allowlist it to permit).";
        }

        if (IsPrivate(addr) && !policy.AllowPrivate)
        {
            return $"URL host '{host}' resolves to a private address ({addr}); " +
                   "private ranges are denied for this integration (allowlist it to permit).";
        }

        return null;
    }

    /// <summary>Link-local / cloud-metadata (169.254.0.0/16, fe80::/10) and the
    /// unspecified address (0.0.0.0 / ::). Never allowlistable.</summary>
    public static bool IsHardBlocked(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var addr = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        if (addr.Equals(IPAddress.Any) || addr.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        if (addr.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = addr.GetAddressBytes();
            // 169.254.0.0/16 link-local (includes 169.254.169.254 metadata).
            return b[0] == 169 && b[1] == 254;
        }

        if (addr.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // fe80::/10 link-local.
            return addr.IsIPv6LinkLocal;
        }

        return false;
    }

    /// <summary>Loopback: IPv4 127.0.0.0/8 and IPv6 ::1.</summary>
    public static bool IsLoopback(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var addr = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return IPAddress.IsLoopback(addr);
    }

    /// <summary>Private ranges: RFC1918 (10/8, 172.16/12, 192.168/16),
    /// CGNAT (100.64/10, RFC6598) and IPv6 unique-local (fc00::/7).</summary>
    public static bool IsPrivate(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var addr = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        if (addr.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = addr.GetAddressBytes();
            if (b[0] == 10)
            {
                return true;                              // 10.0.0.0/8
            }
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            {
                return true;                              // 172.16.0.0/12
            }
            if (b[0] == 192 && b[1] == 168)
            {
                return true;                              // 192.168.0.0/16
            }
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
            {
                return true;                              // 100.64.0.0/10 (CGNAT)
            }
            return false;
        }

        if (addr.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // fc00::/7 unique-local (fc00::/8 + fd00::/8).
            var b0 = addr.GetAddressBytes()[0];
            return (b0 & 0xFE) == 0xFC;
        }

        return false;
    }
}
