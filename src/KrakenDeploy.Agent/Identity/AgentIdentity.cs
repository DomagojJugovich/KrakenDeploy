namespace KrakenDeploy.Agent.Identity;

/// <summary>
/// The persisted agent identity. Serialised to <c>agent.json</c> in the data directory
/// after successful registration. Presence of this file is the signal that the agent is
/// already registered and the one-time token should be ignored.
/// </summary>
public sealed class AgentIdentity
{
    public Guid AgentId { get; set; }

    /// <summary>Long-lived HS256 JWT issued by the server at registration time.</summary>
    public string AgentToken { get; set; } = "";

    /// <summary>
    /// Server URL recorded at registration time. Used to detect config drift.
    /// </summary>
    public string ServerUrl { get; set; } = "";

    /// <summary>
    /// Transport mode assigned by the server at registration time.
    /// Defaults to <c>Reverse</c> for backward compatibility.
    /// </summary>
    public string TransportMode { get; set; } = "Reverse";

    /// <summary>
    /// Blue-green version pin captured from the router's <c>X-KD-Release</c>
    /// response header at registration time (multi-node SaaS; null on
    /// single-instance installs, where no router exists). Echoed as a request
    /// header on the hub connection so a mid-drain reconnect lands back on the
    /// slot holding this agent's in-flight orchestration state. A stale pin is
    /// harmless — the router falls back to the current default release.
    /// </summary>
    public string? ReleaseId { get; set; }
}
