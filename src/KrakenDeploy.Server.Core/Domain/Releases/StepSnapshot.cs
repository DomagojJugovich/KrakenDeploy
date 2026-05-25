using KrakenDeploy.Server.Core.Domain.Processes;

namespace KrakenDeploy.Server.Core.Domain.Releases;

/// <summary>
/// Immutable snapshot of a deployment/runbook step taken at release or run creation time.
/// Stored as jsonb so historical records remain accurate after process edits.
/// </summary>
public sealed class StepSnapshot
{
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

    // ── M14 step-execution knobs (mirror DeploymentStep) ─────────────────
    // Releases freeze these at cut time so historical reproducibility
    // survives subsequent edits to the live process. The jsonb shape
    // adds the new fields with type-default values for old rows — older
    // snapshots simply read as "Success / Required=true / no retries /
    // no timeout / sequential", which preserves the runtime they were
    // created under.

    /// <summary>M14.2 Run Condition — see <see cref="DeploymentStep.Condition"/>.</summary>
    public StepCondition Condition { get; init; } = StepCondition.Success;

    /// <summary>M14.2 Variable-condition expression — see
    /// <see cref="DeploymentStep.ConditionVariableExpression"/>.</summary>
    public string? ConditionVariableExpression { get; init; }

    /// <summary>M14.2 Required — see <see cref="DeploymentStep.Required"/>.
    /// Defaulted to <c>true</c> on the property; older snapshots without
    /// this field also surface as <c>true</c> via System.Text.Json's
    /// "missing property = type default" behaviour ONLY IF the property
    /// is not init-only with a default. With our default the missing
    /// key falls back to the property initializer — pre-M14 rows read
    /// as Required=true, matching the orchestrator's pre-M14 behaviour.
    /// </summary>
    public bool Required { get; init; } = true;

    /// <summary>M14.3 Retry count — see <see cref="DeploymentStep.MaxRetries"/>.</summary>
    public int MaxRetries { get; init; }

    /// <summary>M14.3 Retry delay seconds — see <see cref="DeploymentStep.RetryDelaySeconds"/>.</summary>
    public int RetryDelaySeconds { get; init; }

    /// <summary>M14.2 Timeout seconds — see <see cref="DeploymentStep.TimeoutSeconds"/>.</summary>
    public int TimeoutSeconds { get; init; }

    /// <summary>M14.4 Start trigger — see <see cref="DeploymentStep.StartTrigger"/>.</summary>
    public StepStartTrigger StartTrigger { get; init; } = StepStartTrigger.StartAfterPrevious;
}
