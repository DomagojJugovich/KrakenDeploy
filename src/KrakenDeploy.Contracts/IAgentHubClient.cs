namespace KrakenDeploy.Contracts;

/// <summary>
/// Methods that the server pushes to a connected agent (server → agent direction).
/// Typed client on <c>Hub&lt;IAgentHubClient&gt;</c>; the agent wires these up via
/// <see cref="Microsoft.AspNetCore.SignalR.Client.HubConnection.On{T}"/>.
/// </summary>
public interface IAgentHubClient
{
    /// <summary>Round-trip connectivity check.</summary>
    Task PingAsync();

    /// <summary>
    /// Instructs the agent to execute the given deployment plan.
    /// The agent runs all steps autonomously and reports progress via
    /// <see cref="IAgentHubServer.AppendLogAsync"/> /
    /// <see cref="IAgentHubServer.CompleteDeploymentAsync"/>.
    /// </summary>
    Task RunDeploymentAsync(DeploymentPlan plan);
}
