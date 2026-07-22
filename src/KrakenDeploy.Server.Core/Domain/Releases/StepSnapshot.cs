using KrakenDeploy.Execution;
using KrakenDeploy.Server.Core.Domain.Processes;

namespace KrakenDeploy.Server.Core.Domain.Releases;

/// <summary>
/// Immutable snapshot of a deployment/runbook step taken at release or run creation time.
/// Stored as jsonb so historical records remain accurate after process edits.
/// </summary>
public sealed class StepSnapshot
{
    /// <summary>
    /// M15 — frozen at snapshot time as the corresponding
    /// <see cref="Processes.ProcessStep.Id"/>. Lets
    /// <see cref="ParentStepId"/> form parent-child links inside the
    /// snapshot tree. Pre-M15 snapshots (where this field wasn't set
    /// at cut time) deserialise as <see cref="Guid.Empty"/>, which
    /// cannot be referenced as a parent — so old snapshots behave as
    /// flat lists, matching the runtime they were cut under.
    /// </summary>
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;
    public string StepType { get; init; } = string.Empty;
    public string PackageId { get; init; } = string.Empty;
    public string PackageVersion { get; init; } = string.Empty;
    public List<string> TargetRoles { get; init; } = [];
    public Dictionary<string, string> Config { get; init; } = [];
    public int SortOrder { get; init; }

    /// <summary>The step-package name locked into this release (Phase D-6).
    /// Paired with <see cref="StepPackageVersion"/>; both null together when
    /// no installed package claimed the step type at snapshot time.</summary>
    public string? StepPackageName { get; init; }

    /// <summary>
    /// The step-package version locked into this release (Phase D-6).
    /// <c>null</c> when no step-package claimed this step type at snapshot
    /// time — the agent then uses its hardcoded handler. Once D-8 has
    /// extracted the built-ins, every new release pins a real (name, version)
    /// pair here.
    /// <para>
    /// Pin is permanent: even if newer versions of the step package land
    /// later, the release continues to deploy against this exact one. That's
    /// the contract that makes a release reproducible.
    /// </para>
    /// </summary>
    public string? StepPackageVersion { get; init; }

    // ── M14 step-execution knobs (mirror ProcessStep) ─────────────────
    // Releases freeze these at cut time so historical reproducibility
    // survives subsequent edits to the live process. The jsonb shape
    // adds the new fields with type-default values for old rows — older
    // snapshots simply read as "Success / Required=true / no retries /
    // no timeout / sequential", which preserves the runtime they were
    // created under.

    /// <summary>M14.2 Run Condition — see <see cref="ProcessStep.Condition"/>.</summary>
    public StepCondition Condition { get; init; } = StepCondition.Success;

    /// <summary>M14.2 Variable-condition expression — see
    /// <see cref="ProcessStep.ConditionVariableExpression"/>.</summary>
    public string? ConditionVariableExpression { get; init; }

    /// <summary>M14.2 Required — see <see cref="ProcessStep.Required"/>.
    /// Defaulted to <c>true</c> on the property; older snapshots without
    /// this field also surface as <c>true</c> via System.Text.Json's
    /// "missing property = type default" behaviour ONLY IF the property
    /// is not init-only with a default. With our default the missing
    /// key falls back to the property initializer — pre-M14 rows read
    /// as Required=true, matching the orchestrator's pre-M14 behaviour.
    /// </summary>
    public bool Required { get; init; } = true;

    /// <summary>M14.3 Retry count — see <see cref="ProcessStep.MaxRetries"/>.</summary>
    public int MaxRetries { get; init; }

    /// <summary>M14.3 Retry delay seconds — see <see cref="ProcessStep.RetryDelaySeconds"/>.</summary>
    public int RetryDelaySeconds { get; init; }

    /// <summary>M14.2 Timeout seconds — see <see cref="ProcessStep.TimeoutSeconds"/>.</summary>
    public int TimeoutSeconds { get; init; }

    /// <summary>M14.4 Start trigger — see <see cref="ProcessStep.StartTrigger"/>.</summary>
    public StepStartTrigger StartTrigger { get; init; } = StepStartTrigger.StartAfterPrevious;

    // ── D3 control-flow flags (mirror ProcessStep) ────────────────────────────
    // Frozen at cut time so historical reproducibility survives process edits.
    // Old jsonb snapshots (pre-D3) deserialize these with their type defaults
    // (false / null / null / false), matching the runtime they were cut under —
    // i.e. agent-side execution, no rolling cap, no ForEach loop.

    /// <summary>Leaf/script flag — see <see cref="ProcessStep.RunOnServer"/>.</summary>
    public bool RunOnServer { get; init; }

    /// <summary>Step-group rolling-window cap — see
    /// <see cref="ProcessStep.MaxParallelism"/>.</summary>
    public int? MaxParallelism { get; init; }

    /// <summary>Step-group ForEach collection (unresolved template) — see
    /// <see cref="ProcessStep.ForEachCollection"/>.</summary>
    public string? ForEachCollection { get; init; }

    /// <summary>Step-group ForEach parallel flag — see
    /// <see cref="ProcessStep.ForEachParallel"/>.</summary>
    public bool ForEachParallel { get; init; }

    /// <summary>
    /// M15 — parent step ID in the snapshot tree. Frozen at release-cut
    /// time so subsequent process edits don't reshape the release. Null
    /// for top-level steps (the common case); set for children of a
    /// <see cref="KrakenStepTypes.StepGroup"/>-typed parent step.
    ///
    /// <para>
    /// The snapshot stays a flat list — the parent-child relation lives
    /// in this field. The orchestrator's
    /// <c>DeploymentPlanFlattener</c> reconstructs the tree at deployment
    /// time. Pre-M15 snapshots deserialise with this field absent → it
    /// reads as null and the row behaves as a top-level step, matching
    /// the runtime they were created under.
    /// </para>
    /// </summary>
    public Guid? ParentStepId { get; init; }
}
