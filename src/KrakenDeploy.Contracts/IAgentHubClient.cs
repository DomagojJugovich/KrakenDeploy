using KrakenDeploy.Contracts.Adhoc;

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

    /// <summary>
    /// B6 CONTRACT CHANGE — instructs the agent to cooperatively abort the
    /// in-flight task <paramref name="taskId"/> (a deployment or a runbook run;
    /// the agent knows it as <see cref="DeploymentPlan.DeploymentId"/>). The
    /// agent signals the task's cancellation token — which kills the running
    /// step's process tree — and reports a failed completion for the attempt;
    /// the server has already recorded the Cancelled verdict, so that late
    /// completion is swallowed by the terminal guard. Unknown / already
    /// finished taskId is a no-op (the push is best-effort; an offline agent
    /// falls back to the wave-boundary cancel semantics).
    /// </summary>
    Task CancelDeploymentAsync(Guid taskId, string? reason);

    /// <summary>
    /// M11.E.7 — instructs the agent to verify and run an operator-approved
    /// ad-hoc script. The agent MUST verify
    /// <see cref="AdhocScriptCommand.Signature"/> via
    /// <see cref="AdhocScriptSigner.Verify"/> against its configured
    /// <c>Adhoc:TrustedPublicKey</c> BEFORE executing; on signature mismatch
    /// the agent refuses to run and reports the failure via
    /// <see cref="IAgentHubServer.ReportAdhocResultAsync"/>. The script runs
    /// once on this target only; the server fans the same command out to
    /// every target in the session's frozen set in parallel.
    /// </summary>
    Task RunAdhocScriptAsync(AdhocScriptCommand command);
}
