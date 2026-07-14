using KrakenDeploy.Contracts.Adhoc;

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
    /// Streams a single log line from an executing step. The server persists it
    /// (staged per step for compaction) and broadcasts it to the UI in real time.
    /// <paramref name="stepIndex"/> is the plan-level step index the line belongs
    /// to, or -1 for plan-level lines.
    /// </summary>
    Task AppendLogAsync(Guid deploymentId, int stepIndex, string level, string message);

    /// <summary>
    /// Called by the agent when all steps have finished (or a step has failed).
    /// The server transitions the deployment to <c>Succeeded</c> or <c>Failed</c>.
    /// </summary>
    Task CompleteDeploymentAsync(Guid deploymentId, bool success, string? errorMessage);

    /// <summary>
    /// M14.4 — reports the per-step boundary back to the server: success/
    /// failure outcome, optional error message, and any output variables
    /// captured via <c>Set-OctopusVariable</c> / <c>##octopus[setVariable]</c>
    /// markers during the step.
    ///
    /// <para>
    /// Replaces the pre-M14.4 <c>ReportStepOutputVariablesAsync</c>: the
    /// orchestrator now needs per-step attribution to apply the Required
    /// gate against individual steps inside a parallel wave (not the
    /// whole wave conservatively). Server persists outputs upserted by
    /// <c>(deploymentId, stepName, name)</c> (same shape as before),
    /// and records the per-step outcome against the pending sub-plan so
    /// <c>CompleteDeploymentAsync</c> at wave end has full attribution.
    /// </para>
    ///
    /// <para>
    /// <paramref name="stepIndex"/> is <see cref="DeploymentStepPlan.Index"/>
    /// — stable across the deployment so the orchestrator can find the
    /// step's <c>StepSnapshot</c> by index without name-collision risk
    /// inside ForEach iterations (M15) or duplicate-name authoring.
    /// </para>
    /// </summary>
    /// <param name="sensitiveOutputNames">
    /// T0-6: subset of <paramref name="outputVariables"/> keys emitted with
    /// <c>Set-OctopusVariable -sensitive</c>. The server encrypts those values
    /// at rest and masks them in the UI. Empty means none are sensitive.
    /// </param>
    Task ReportStepCompletedAsync(
        Guid deploymentId,
        int stepIndex,
        string stepName,
        bool success,
        string? errorMessage,
        Dictionary<string, string> outputVariables,
        List<string> sensitiveOutputNames);

    /// <summary>
    /// M11.E.7 — reports an ad-hoc script's per-target outcome back to the
    /// server. Called once per target after the agent has either run the
    /// signed script (<see cref="AdhocScriptResult.AgentError"/> = null) or
    /// refused to run it (signature mismatch, missing public key, runtime
    /// exception). The server resolves the target id from this connection's
    /// <c>NameIdentifier</c> claim, looks up the matching
    /// <see cref="AdhocScriptCommand.SessionId"/> /
    /// <see cref="AdhocScriptCommand.IterNumber"/> slot in the pending-adhoc
    /// registry, and resolves the dispatcher's awaiting TCS.
    /// </summary>
    Task ReportAdhocResultAsync(AdhocScriptResult result);
}
