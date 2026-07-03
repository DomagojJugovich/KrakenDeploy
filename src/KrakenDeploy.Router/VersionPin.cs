namespace KrakenDeploy.Router;

/// <summary>
/// Reads and issues the release pin (§3 of the design): the
/// <c>__Host-kd_ver</c> cookie for browsers, the <c>X-KD-Release</c> header for
/// agents. The explicit header wins over a cookie (it is a deliberate pin —
/// e.g. the pre-flip health-gate).
/// </summary>
public static class VersionPin
{
    /// <summary>Version cookie over HTTPS. The <c>__Host-</c> prefix binds it to the exact origin.</summary>
    public const string CookieName = "__Host-kd_ver";

    /// <summary>
    /// Fallback cookie name over plain HTTP (local dev / smoke only) — browsers
    /// refuse <c>__Host-</c> cookies without <c>Secure</c>.
    /// </summary>
    public const string InsecureCookieName = "kd_ver";

    /// <summary>Agent pin header, echoed on the persistent connection (§3).</summary>
    public const string HeaderName = "X-KD-Release";

    public static PinExtraction Extract(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Headers.TryGetValue(HeaderName, out var header)
            && !string.IsNullOrWhiteSpace(header))
        {
            return new PinExtraction(header.ToString(), FromHeader: true);
        }

        if (request.Cookies.TryGetValue(CookieName, out var cookie)
            && !string.IsNullOrEmpty(cookie))
        {
            return new PinExtraction(cookie, FromHeader: false);
        }

        if (request.Cookies.TryGetValue(InsecureCookieName, out cookie)
            && !string.IsNullOrEmpty(cookie))
        {
            return new PinExtraction(cookie, FromHeader: false);
        }

        return new PinExtraction(null, FromHeader: false);
    }

    /// <summary>
    /// Appends the version cookie. No signing — every live release is
    /// schema-compatible (additive), so a tampered value at worst maps to the
    /// default; the pin is a convenience, not a security boundary (§3).
    /// </summary>
    public static void Issue(HttpContext context, string releaseId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseId);

        var secure = context.Request.IsHttps
            || (context.Request.Headers.TryGetValue("X-Forwarded-Proto", out var proto)
                && proto.ToString().Contains("https", StringComparison.OrdinalIgnoreCase));

        context.Response.Cookies.Append(
            secure ? CookieName : InsecureCookieName,
            releaseId,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = secure,
                SameSite = SameSiteMode.Lax,
                Path = "/",
            });
    }
}

/// <summary>
/// An extracted pin and where it came from. The source matters: a pin to a
/// <c>Deploying</c> (pre-health-gate) release is honored only from the explicit
/// header (operator tooling), never from a browser cookie.
/// </summary>
public sealed record PinExtraction(string? Value, bool FromHeader);
