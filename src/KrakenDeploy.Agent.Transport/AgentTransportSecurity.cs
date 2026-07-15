namespace KrakenDeploy.Agent.Transport;

/// <summary>
/// A8/T1-12 — the agent's server-URL transport policy. The same server URL backs
/// both the SignalR control tunnel and the gRPC package/artifact channels, so a
/// single check covers every transport. https is required; cleartext http:// is
/// refused unless the operator has explicitly set the dev override
/// (<c>Server:AllowInsecureHttp</c>). Returns a result (no throw) so startup can
/// log a clear message and stop, while <see cref="GrpcChannelFactory"/> turns a
/// failure into an exception at the last line of defense.
/// </summary>
public static class AgentTransportSecurity
{
    public static (bool Ok, string? Error) Validate(string? serverUrl, bool allowInsecureHttp)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri))
        {
            return (false, $"Server URL '{serverUrl}' is not a valid absolute URL.");
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return (true, null);
        }

        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            return allowInsecureHttp
                ? (true, null)
                : (false,
                    "Server URL uses cleartext http://; agent transport requires https. " +
                    "Set Server:AllowInsecureHttp=true ONLY for local development to override.");
        }

        return (false,
            $"Server URL scheme '{uri.Scheme}' is not supported — use https " +
            "(or http with Server:AllowInsecureHttp for local development).");
    }
}
