using System.Text.RegularExpressions;

namespace KrakenDeploy.Server.Observability;

/// <summary>
/// A8/T1-12 — strips the agent bearer token from a request path/query before it
/// is written to the request log. SignalR delivers the agent JWT as
/// <c>?access_token=…</c> (WebSocket upgrades can't carry headers). Serilog's
/// request logger excludes the query by default, so this is defense-in-depth: it
/// guarantees the token can never surface in the log even if query logging is
/// later turned on.
/// </summary>
public static partial class RequestLogRedaction
{
    [GeneratedRegex(
        "(access_token=)[^&\\s]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AccessTokenParam();

    /// <summary>
    /// Replaces the value of any <c>access_token</c> query parameter with
    /// <c>REDACTED</c>, leaving the rest of the path/query intact.
    /// </summary>
    public static string RedactTokens(string? requestPath) =>
        string.IsNullOrEmpty(requestPath)
            ? requestPath ?? string.Empty
            : AccessTokenParam().Replace(requestPath, "$1REDACTED");
}
