using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Runbooks;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data.Services;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// D1 engine merge — a kind-branched view over a <see cref="ServerTask"/> that
/// the unified orchestrator (<see cref="DeploymentWorker"/>) consumes so the
/// spine reads ONE shape regardless of kind. Encapsulates the load-bearing
/// forks between a <see cref="Deployment"/> and a <see cref="RunbookRun"/>:
/// process-snapshot source, variable-resolution strategy (frozen release
/// snapshot vs live resolve), system-variable builder, the deployment-only
/// gates (freeze, variable-snapshot refusal, offline drop, AI diagnosis),
/// retention keep source, and the audit vocabulary. Snapshots stay where they
/// live (locked decision N4, 2026-07-18) — no jsonb column moves onto
/// <see cref="ServerTask"/>.
/// </summary>
internal interface ITaskDispatchSource
{
    /// <summary>The task's kind.</summary>
    ServerTaskKind Kind { get; }

    /// <summary>The process snapshot to execute — the frozen
    /// <c>Release.ProcessSnapshot</c> for a deployment, the
    /// <c>RunbookRun.ProcessSnapshot</c> frozen at trigger time for a run.</summary>
    IReadOnlyList<StepSnapshot> ProcessSnapshot { get; }

    /// <summary>The frozen definitions used for deployment prompts; null for runbooks.</summary>
    IReadOnlyList<VariableSnapshot>? PromptVariableSnapshot { get; }

    /// <summary>Deployments consult the deployment-freeze gate before dispatch;
    /// runbook runs skip it (Octopus parity — runbooks run during freeze
    /// windows). Locked decision 5.</summary>
    bool AppliesFreezeGate { get; }

    /// <summary>Only deployments support offline-drop delivery (the bundle is a
    /// physical artifact for a specific machine). A runbook run targeting an
    /// offline-drop machine is not a supported combination.</summary>
    bool SupportsOfflineDrop { get; }

    /// <summary>The kind-appropriate audit-event vocabulary (never emits the
    /// other kind's event names — see <see cref="TaskAuditVocabulary"/>).</summary>
    TaskAuditVocabulary Audit { get; }

    /// <summary>
    /// Deployment: returns a failure message when the release has no variable
    /// snapshot (<c>VariableSnapshotUpdatedUtc == null</c>) so dispatch is
    /// refused; returns <c>null</c> to proceed. Runbook: always <c>null</c>
    /// (variables are resolved live, there is no snapshot to be missing).
    /// </summary>
    string? VariableSnapshotRefusal();

    /// <summary>
    /// Resolves deployment-wide + per-step variables for one target. Deployment:
    /// from the frozen <c>Release.VariableSnapshot</c> (channel-scoped). Runbook:
    /// live from the project's current variables (not channel-scoped).
    /// </summary>
    Task<StepScopedResolution> ResolveVariablesAsync(
        VariableService variableService,
        DeploymentTarget target,
        IReadOnlyList<(Guid StepId, string StepName)> steps,
        IReadOnlyList<Guid>? tenantTagIds,
        CancellationToken ct);

    /// <summary>Builds the <c>Octopus.*</c> system-variable dictionary for one
    /// target via the kind-correct builder.</summary>
    IReadOnlyDictionary<string, string> BuildSystemVariables(
        DeploymentTarget target,
        string? serverBaseUrl,
        IReadOnlyList<string>? tenantTagCanonicals);
}

/// <summary>Deployment-kind dispatch source. Wraps a <see cref="Deployment"/>
/// with its <c>Release</c> (and <c>Release.Project</c>), <c>Environment</c> and
/// <c>Tenant</c> navigations loaded.</summary>
internal sealed class DeploymentDispatchSource(Deployment deployment) : ITaskDispatchSource
{
    public ServerTaskKind Kind => ServerTaskKind.Deployment;
    public IReadOnlyList<StepSnapshot> ProcessSnapshot => deployment.Release.ProcessSnapshot;
    public IReadOnlyList<VariableSnapshot>? PromptVariableSnapshot => deployment.Release.VariableSnapshot;
    public bool AppliesFreezeGate => true;
    public bool SupportsOfflineDrop => true;
    public TaskAuditVocabulary Audit => TaskAuditVocabulary.Deployment;

    public string? VariableSnapshotRefusal()
        => deployment.Release.VariableSnapshotUpdatedUtc is not null
            ? null
            : $"Release '{deployment.Release.Version}' has no variable snapshot. " +
              "Open the release in the UI and click 'Update Variables' to freeze " +
              "the project's current variables into the release, then re-deploy.";

    public Task<StepScopedResolution> ResolveVariablesAsync(
        VariableService variableService,
        DeploymentTarget target,
        IReadOnlyList<(Guid StepId, string StepName)> steps,
        IReadOnlyList<Guid>? tenantTagIds,
        CancellationToken ct)
        => variableService.ResolveFromSnapshotWithStepsAsync(
            deployment.Release.VariableSnapshot,
            deployment.EnvironmentId,
            target.Id,
            target.Roles,
            deployment.TenantId,
            deployment.Release.ChannelId,
            steps,
            tenantTagIds: tenantTagIds,
            ct: ct);

    public IReadOnlyDictionary<string, string> BuildSystemVariables(
        DeploymentTarget target,
        string? serverBaseUrl,
        IReadOnlyList<string>? tenantTagCanonicals)
        => OctopusSystemVariablesBuilder.BuildForDeployment(
            deployment,
            deployment.Release,
            deployment.Release.Project,
            deployment.Environment,
            target,
            deployment.Tenant,
            deployment.Release.ProcessSnapshot,
            serverBaseUrl,
            tenantTagCanonicals);
}

/// <summary>Runbook-run dispatch source. Wraps a <see cref="RunbookRun"/> with
/// its <c>Runbook</c> (and <c>Runbook.Project</c>), <c>Environment</c> and
/// <c>Tenant</c> navigations loaded. Variables resolve live (no release
/// snapshot); the freeze gate, offline drop, AI diagnosis and variable-snapshot
/// refusal are all skipped.</summary>
internal sealed class RunbookRunDispatchSource(RunbookRun run) : ITaskDispatchSource
{
    public ServerTaskKind Kind => ServerTaskKind.RunbookRun;
    public IReadOnlyList<StepSnapshot> ProcessSnapshot => run.ProcessSnapshot;
    public IReadOnlyList<VariableSnapshot>? PromptVariableSnapshot => null;
    public bool AppliesFreezeGate => false;
    public bool SupportsOfflineDrop => false;
    public TaskAuditVocabulary Audit => TaskAuditVocabulary.RunbookRun;

    public string? VariableSnapshotRefusal() => null;

    public Task<StepScopedResolution> ResolveVariablesAsync(
        VariableService variableService,
        DeploymentTarget target,
        IReadOnlyList<(Guid StepId, string StepName)> steps,
        IReadOnlyList<Guid>? tenantTagIds,
        CancellationToken ct)
        => variableService.ResolveWithStepsAsync(
            run.ProjectId,
            run.EnvironmentId,
            target.Id,
            target.Roles,
            run.TenantId,
            channelId: null,   // runbook runs are not channel-scoped
            steps,
            tenantTagIds: tenantTagIds,
            ct: ct);

    public IReadOnlyDictionary<string, string> BuildSystemVariables(
        DeploymentTarget target,
        string? serverBaseUrl,
        IReadOnlyList<string>? tenantTagCanonicals)
        => OctopusSystemVariablesBuilder.BuildForRunbookRun(
            run,
            run.Runbook,
            run.Runbook.Project,
            run.Environment,
            target,
            run.Tenant,
            run.ProcessSnapshot,
            serverBaseUrl,
            tenantTagCanonicals);
}
