using System.Net.Http.Headers;
using Grpc.Net.Client;

namespace KrakenDeploy.Agent.Transport;

/// <summary>
/// A8/T1-12 — single construction point for the agent's gRPC channels. Enforces
/// the transport-security policy (<see cref="AgentTransportSecurity"/>) and — only
/// when deliberately talking to an <c>http://</c> server under the dev override —
/// enables cleartext HTTP/2 (h2c). This replaces the three copies of an
/// UNCONDITIONAL, process-global <c>Http2UnencryptedSupport</c> switch that
/// previously let any channel silently downgrade to cleartext regardless of
/// scheme or environment.
/// </summary>
public static class GrpcChannelFactory
{
    public static GrpcChannel Create(string serverUrl, string agentToken, bool allowInsecureHttp)
    {
        var (ok, error) = AgentTransportSecurity.Validate(serverUrl, allowInsecureHttp);
        if (!ok)
        {
            throw new InvalidOperationException(error);
        }

        // h2c is OFF by default in .NET; enable it ONLY for an http:// server that
        // the operator has explicitly opted into. https channels never touch the
        // process-global switch, so a production agent can never speak cleartext.
        if (new Uri(serverUrl).Scheme == Uri.UriSchemeHttp)
        {
            AppContext.SetSwitch(
                "System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        }

        var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", agentToken);

        return GrpcChannel.ForAddress(serverUrl, new GrpcChannelOptions
        {
            HttpClient = httpClient,
            DisposeHttpClient = true,
        });
    }
}
