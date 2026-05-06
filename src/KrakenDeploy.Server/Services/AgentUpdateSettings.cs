namespace KrakenDeploy.Server.Services;

/// <summary>
/// Bound to the "AgentUpdate" configuration section. Controls where agent
/// binaries and the version manifest are stored on the server.
/// </summary>
public sealed class AgentUpdateSettings
{
    /// <summary>
    /// Path to the directory containing agent binaries and <c>version.json</c>.
    /// Relative paths are resolved against the server's data directory.
    /// Defaults to <c>agents</c>.
    /// </summary>
    public string BinariesPath { get; set; } = "agents";
}
