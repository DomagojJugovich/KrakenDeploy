namespace KrakenDeploy.Contracts;

/// <summary>
/// Custom HTTP headers shared between KrakenDeploy clients (CLI) and the server.
/// </summary>
public static class KrakenHttpHeaders
{
    /// <summary>Identifies the client kind so the server can attribute task
    /// provenance (e.g. <c>cause = Cli</c> vs <c>cause = Api</c>). The API-key
    /// principal is otherwise identical across CLI, generic REST, and MCP.</summary>
    public const string ClientKind = "X-Kraken-Client";

    /// <summary>Value of <see cref="ClientKind"/> sent by the KrakenDeploy CLI.</summary>
    public const string ClientKindCli = "cli";
}
