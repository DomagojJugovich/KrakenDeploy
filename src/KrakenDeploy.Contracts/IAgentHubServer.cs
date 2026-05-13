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

    /// <summary>
    /// Streams a single log line from an executing deployment step.
    /// The server persists it and broadcasts it to the UI in real time.
    /// </summary>
    Task AppendLogAsync(Guid deploymentId, string level, string message);

    /// <summary>
    /// Called by the agent when all steps have finished (or a step has failed).
    /// The server transitions the deployment to <c>Succeeded</c> or <c>Failed</c>.
    /// </summary>
    Task CompleteDeploymentAsync(Guid deploymentId, bool success, string? errorMessage);

    /// <summary>
    /// Called after each step that emitted at least one
    /// <c>Set-OctopusVariable</c> / <c>##octopus[setVariable]</c> marker on stdout.
    /// The server persists the captured key/value pairs against the deployment
    /// and step so they appear on the deployment detail page and (later) can be
    /// referenced from other deployments / runbooks.
    /// </summary>
    Task ReportStepOutputVariablesAsync(
        Guid deploymentId, string stepName, Dictionary<string, string> outputVariables);
}
