using System.Text.Json;

namespace KrakenDeploy.Agent.Services;

/// <summary>
/// A8 sliding refresh — WHEN to renew the agent bearer token. Pure functions so
/// the schedule is unit-testable without a host. The token's validity window is
/// read straight from the JWT payload (<c>nbf</c>/<c>exp</c>, unix seconds) with
/// no signature check — the agent is only scheduling around timestamps in its
/// OWN token; the server re-validates everything on the refresh call.
/// </summary>
internal static class AgentTokenRefreshPolicy
{
    /// <summary>
    /// Extracts the validity window from a compact JWT. Returns false on any
    /// malformed input (wrong segment count, bad base64url, missing claims) —
    /// the caller then refreshes eagerly rather than never.
    /// </summary>
    public static bool TryGetValidityWindow(
        string token, out DateTimeOffset notBefore, out DateTimeOffset expires)
    {
        notBefore = default;
        expires = default;

        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        try
        {
            var payload = Base64UrlDecode(parts[1]);
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("exp", out var expEl) ||
                !doc.RootElement.TryGetProperty("nbf", out var nbfEl) ||
                !expEl.TryGetInt64(out var exp) ||
                !nbfEl.TryGetInt64(out var nbf))
            {
                return false;
            }

            notBefore = DateTimeOffset.FromUnixTimeSeconds(nbf);
            expires = DateTimeOffset.FromUnixTimeSeconds(exp);
            return expires > notBefore;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>
    /// Refresh once the token is past HALF of its validity window. Early enough
    /// that an agent offline for up to half the lifetime still renews in time;
    /// late enough that refreshes stay rare (one per half-lifetime per agent).
    /// </summary>
    public static bool ShouldRefresh(DateTimeOffset now, DateTimeOffset notBefore, DateTimeOffset expires) =>
        now >= notBefore + (expires - notBefore) / 2;

    private static byte[] Base64UrlDecode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
