namespace KrakenDeploy.Contracts;

/// <summary>
/// Methods that the agent calls on the server hub (agent → server direction).
/// Implemented by <c>AgentHub</c> in <c>KrakenDeploy.Server.Transport</c>.
/// The agent uses <see cref="Microsoft.AspNetCore.SignalR.Client.HubConnection"/>
/// to invoke these by name.
/// </summary>
public interface IAgentHubServer
{
    /// <summary>Called once after the SignalR connection is up to supply machine info.</summary>
    Task RegisterAsync(AgentRegistrationRequest request);

    /// <summary>Called every 30 s to keep <c>LastSeenUtc</c> fresh.</summary>
    Task HeartbeatAsync(HeartbeatRequest request);

    /// <summary>Reports an agent-observed status change (e.g. shutting down).</summary>
    Task ReportStatusAsync(string status);

    /// <summary>Streams a log chunk for an in-progress deployment.</summary>
    Task AppendLogAsync(Guid deploymentId, string chunk);
}
