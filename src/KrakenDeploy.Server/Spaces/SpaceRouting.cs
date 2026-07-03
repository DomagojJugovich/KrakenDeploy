using Microsoft.AspNetCore.Http;

namespace KrakenDeploy.Server.Spaces;

/// <summary>
/// Pure helpers for the Space-in-URL scheme: every Blazor page route lives under
/// <c>/s/{spaceSlug}/…</c> so the active Space is carried by the URL (per-tab,
/// refresh-stable, bookmarkable) rather than a browser-global cookie.
/// <para>
/// The <c>/s/{SpaceSlug}/…</c> prefix is a real <c>@page</c> route template on
/// every page (matched directly by the Blazor router — nothing is rewritten);
/// <c>SpaceScopedComponentBase</c> reads the slug and applies the active Space.
/// <c>SpaceUrlRedirectMiddleware</c> only 302-redirects a bare (unprefixed) path
/// to the Default Space.
/// </para>
/// </summary>
public static class SpaceRouting
{
    /// <summary>The leading segment that marks a Space-scoped URL.</summary>
    public const string Prefix = "/s/";

    /// <summary>
    /// Splits an incoming path. For <c>/s/{slug}/rest…</c> returns the slug and
    /// the remainder path (always starting with <c>/</c>, or <c>/</c> for the
    /// Space root <c>/s/{slug}</c>). For anything else returns
    /// <c>(null, originalPath)</c>.
    /// </summary>
    public static (string? Slug, string Remainder) Split(string path)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return (null, string.IsNullOrEmpty(path) ? "/" : path);
        }

        var afterPrefix = path[Prefix.Length..];           // "{slug}/rest" or "{slug}"
        var slash = afterPrefix.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            var onlySlug = afterPrefix;
            return string.IsNullOrEmpty(onlySlug) ? (null, path) : (onlySlug, "/");
        }

        var slug = afterPrefix[..slash];
        if (string.IsNullOrEmpty(slug))
        {
            return (null, path);
        }
        var rest = afterPrefix[slash..];                   // starts with '/'
        return (slug, rest.Length == 0 ? "/" : rest);
    }

    /// <summary>
    /// Builds a Space-prefixed path: <c>/s/{slug}{relativePath}</c>. The
    /// <paramref name="relativePath"/> is the existing unprefixed app path
    /// (e.g. <c>/projects</c>); <c>/</c> maps to the Space root <c>/s/{slug}</c>.
    /// </summary>
    public static string BuildPath(string slug, string relativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(slug);
        if (string.IsNullOrEmpty(relativePath) || relativePath == "/")
        {
            return $"{Prefix}{slug}";
        }
        return relativePath[0] == '/'
            ? $"{Prefix}{slug}{relativePath}"
            : $"{Prefix}{slug}/{relativePath}";
    }

    /// <summary>
    /// True for paths that must NOT be Space-scoped: the API/CLI surface, the
    /// Blazor framework + negotiate, auth endpoints, hubs, gRPC, health, and static
    /// assets. Everything else is a Blazor page route and gets the
    /// <c>/s/{slug}</c> treatment.
    /// </summary>
    public static bool IsSpaceAgnostic(PathString path)
    {
        if (!path.HasValue)
        {
            return true;
        }

        var value = path.Value!;

        // OIDC callbacks are "/signin-{scheme}" — a single segment, so match the
        // literal prefix rather than a path segment.
        if (value.StartsWith("/signin-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var prefix in AgnosticPrefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Static assets (fingerprinted files served from the root): treat any
        // request with a file extension in the last segment as an asset.
        var lastSlash = value.LastIndexOf('/');
        var lastSegment = lastSlash >= 0 ? value[(lastSlash + 1)..] : value;
        return lastSegment.Contains('.', StringComparison.Ordinal);
    }

    private static readonly string[] AgnosticPrefixes =
    [
        "/api",
        "/_blazor",
        "/_framework",
        "/_content",
        "/hubs",
        "/hangfire",
        "/healthz",
        // Blue-green slot telemetry — an infra probe endpoint like /healthz
        // (queried by the drain-watcher), never a Space-scoped page.
        "/slot-metrics",
        "/mcp",
        "/login",
        "/logout",
        "/Error",
    ];
}
