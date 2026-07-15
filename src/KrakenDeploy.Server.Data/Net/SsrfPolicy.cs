using System.Net;
using System.Net.Sockets;

namespace KrakenDeploy.Server.Data.Net;

/// <summary>
/// Per-integration SSRF policy. The default posture is <b>deny</b>: loopback and
/// private ranges (RFC1918 / CGNAT / IPv6 ULA) are refused unless an operator
/// opts in via <see cref="AllowLoopback"/> / <see cref="AllowPrivate"/> or lists
/// a specific host in <see cref="AllowedHosts"/>.
/// <para>
/// Link-local / cloud-metadata (169.254.0.0/16 incl. 169.254.169.254, fe80::/10)
/// and the unspecified address are <b>hard-blocked</b> in <see cref="SsrfGuard"/>
/// and can never be re-enabled through this policy — allowlisting them would
/// re-open the metadata-exfil vector this guard exists to close.
/// </para>
/// </summary>
public sealed class SsrfPolicy
{
    /// <summary>Permit 127.0.0.0/8 and ::1. Default false; true only where a
    /// local co-resident service is a legitimate target (e.g. a local
    /// Ollama / LM Studio behind the AI integration).</summary>
    public bool AllowLoopback { get; set; }

    /// <summary>Permit RFC1918 (10/8, 172.16/12, 192.168/16), CGNAT (100.64/10)
    /// and IPv6 ULA (fc00::/7). Default false — on-prem operators allowlist
    /// specific internal receivers via <see cref="AllowedHosts"/> instead of
    /// opening the whole private space.</summary>
    public bool AllowPrivate { get; set; }

    /// <summary>Explicit per-integration allowlist. Each entry is one of:
    /// a hostname (matched case-insensitively against the request host),
    /// a literal IP address, or a CIDR block (matched against the resolved
    /// address). A match bypasses the loopback / private denials, but NOT the
    /// hard block on link-local/metadata/unspecified.</summary>
    public string[] AllowedHosts { get; set; } = [];

    /// <summary>True when <paramref name="host"/> (the request host) is listed
    /// verbatim as a hostname entry in <see cref="AllowedHosts"/>.</summary>
    public bool MatchesHost(string host)
    {
        foreach (var entry in AllowedHosts)
        {
            if (string.IsNullOrWhiteSpace(entry) || entry.Contains('/'))
            {
                continue; // CIDR — handled by MatchesAddress
            }
            if (IPAddress.TryParse(entry, out _))
            {
                continue; // literal IP — handled by MatchesAddress
            }
            if (string.Equals(entry.Trim(), host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>True when <paramref name="address"/> matches a literal-IP or
    /// CIDR entry in <see cref="AllowedHosts"/>.</summary>
    public bool MatchesAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var addr = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        foreach (var entry in AllowedHosts)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            var trimmed = entry.Trim();
            if (trimmed.Contains('/'))
            {
                if (TryMatchCidr(trimmed, addr))
                {
                    return true;
                }
            }
            else if (IPAddress.TryParse(trimmed, out var literal))
            {
                var lit = literal.IsIPv4MappedToIPv6 ? literal.MapToIPv4() : literal;
                if (lit.Equals(addr))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Matches <paramref name="address"/> against a <c>base/prefix</c>
    /// CIDR string. Returns false on any malformed entry (fail-closed).</summary>
    private static bool TryMatchCidr(string cidr, IPAddress address)
    {
        var slash = cidr.IndexOf('/');
        var basePart = cidr[..slash];
        var prefixPart = cidr[(slash + 1)..];

        if (!IPAddress.TryParse(basePart, out var baseAddr)
            || !int.TryParse(prefixPart, out var prefixLen)
            || prefixLen < 0)
        {
            return false;
        }

        baseAddr = baseAddr.IsIPv4MappedToIPv6 ? baseAddr.MapToIPv4() : baseAddr;
        if (baseAddr.AddressFamily != address.AddressFamily)
        {
            return false;
        }

        var baseBytes = baseAddr.GetAddressBytes();
        var addrBytes = address.GetAddressBytes();
        if (baseBytes.Length != addrBytes.Length || prefixLen > baseBytes.Length * 8)
        {
            return false;
        }

        var fullBytes = prefixLen / 8;
        for (var i = 0; i < fullBytes; i++)
        {
            if (baseBytes[i] != addrBytes[i])
            {
                return false;
            }
        }

        var remainingBits = prefixLen % 8;
        if (remainingBits != 0)
        {
            var mask = (byte)(0xFF << (8 - remainingBits));
            if ((baseBytes[fullBytes] & mask) != (addrBytes[fullBytes] & mask))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Bound from the <c>Ssrf</c> configuration section (<see cref="SectionName"/>).
/// One <see cref="SsrfPolicy"/> per outbound integration so each can be tightened
/// or loosened independently. All default to deny-loopback/deny-private except
/// <see cref="Ai"/>, which defaults <see cref="SsrfPolicy.AllowLoopback"/> true so
/// a co-resident local model server (Ollama / LM Studio on 127.0.0.1) keeps
/// working out of the box.
/// </summary>
public sealed class SsrfOptions
{
    public const string SectionName = "Ssrf";

    /// <summary>Webhook subscription delivery (<c>WebhookTransport</c>).</summary>
    public SsrfPolicy Webhook { get; set; } = new();

    /// <summary>GitHub step-package / step-template catalog fetches.</summary>
    public SsrfPolicy StepCatalog { get; set; } = new();

    /// <summary>OIDC Authority / discovery-document fetch.</summary>
    public SsrfPolicy Oidc { get; set; } = new();

    /// <summary>AI provider endpoint. Loopback allowed by default for local
    /// OpenAI-compatible servers; private ranges still deny-by-default.</summary>
    public SsrfPolicy Ai { get; set; } = new() { AllowLoopback = true };
}
