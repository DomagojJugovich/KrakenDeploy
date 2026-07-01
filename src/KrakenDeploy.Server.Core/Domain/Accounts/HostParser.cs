namespace KrakenDeploy.Server.Core.Domain.Accounts;

/// <summary>
/// Pure helper for extracting the single account subdomain label from a request
/// host, given the platform base domain. A wildcard matches exactly one label
/// (<c>acme.krakendeploy.com</c> → <c>acme</c>); the apex and multi-label hosts
/// resolve to <c>null</c> (control-plane / not a tenant).
/// </summary>
public static class HostParser
{
    /// <summary>
    /// Returns the single subdomain label of <paramref name="host"/> under
    /// <paramref name="baseDomain"/>, or <c>null</c> when <paramref name="host"/> is
    /// the apex, is not under the base domain, or carries more than one label.
    /// Port is stripped and comparison is case-insensitive.
    /// </summary>
    public static string? ExtractSubdomain(string? host, string? baseDomain)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(baseDomain))
        {
            return null;
        }

        var h = host;
        var colon = h.IndexOf(':');
        if (colon >= 0)
        {
            h = h[..colon];
        }

        h = h.Trim().TrimEnd('.').ToLowerInvariant();
        var bd = baseDomain.Trim().TrimEnd('.').ToLowerInvariant();

        if (h.Length == 0 || bd.Length == 0 || h == bd)
        {
            return null; // apex (or empty) → control-plane host, not a tenant
        }

        var suffix = "." + bd;
        if (!h.EndsWith(suffix, StringComparison.Ordinal))
        {
            return null; // not under the configured base domain
        }

        var label = h[..^suffix.Length];
        if (label.Length == 0 || label.Contains('.'))
        {
            return null; // empty or multi-label → wildcard matches one label only
        }

        return label;
    }
}
