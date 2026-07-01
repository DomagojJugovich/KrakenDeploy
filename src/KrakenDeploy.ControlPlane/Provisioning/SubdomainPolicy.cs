using System.Text.RegularExpressions;

namespace KrakenDeploy.ControlPlane.Provisioning;

/// <summary>
/// Subdomain format + reserved-word policy (§10, §15). A subdomain must be a valid
/// single DNS label and must not collide with a reserved control-plane host. Keeping
/// the reserved hosts explicit also matches the DNS/TLS design (reserved hosts
/// override the wildcard, §11).
/// </summary>
public static partial class SubdomainPolicy
{
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "www", "api", "auth", "app", "signup", "admin", "static", "assets", "cdn",
        "mail", "smtp", "ftp", "ns", "ns1", "ns2", "mx", "billing", "id", "status",
        "support", "help", "docs", "blog", "dashboard", "control", "controlplane",
        "default", "localhost", "test", "internal", "kraken",
    };

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex LabelRegex();

    /// <summary>
    /// Normalizes (trim + lower-case) and validates <paramref name="raw"/>. Returns
    /// the normalized subdomain, or an error describing why it is unacceptable.
    /// </summary>
    public static (string? Subdomain, string? Error) Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (null, "Subdomain is required.");
        }

        var s = raw.Trim().ToLowerInvariant();

        if (!LabelRegex().IsMatch(s))
        {
            return (null,
                "Subdomain must be a single DNS label: lower-case letters, digits and " +
                "hyphens, 1–63 chars, not starting or ending with a hyphen.");
        }

        if (Reserved.Contains(s))
        {
            return (null, $"Subdomain '{s}' is reserved.");
        }

        return (s, null);
    }
}
