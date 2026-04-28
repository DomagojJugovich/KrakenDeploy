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
}
