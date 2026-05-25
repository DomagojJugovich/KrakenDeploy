namespace KrakenDeploy.Server.Core.Domain.Processes;

/// <summary>
/// Per-step "Run Condition" knob (M14.2). Decides whether a step runs
/// based on the outcome of prior steps in the same deployment.
///
/// <para>
/// Mirrors Octopus's <c>Condition</c> top-level field on the deployment
/// process step (verified against argosy-process.json fixture). The
/// importer reads the string directly into this enum so a round-tripped
/// process keeps its semantics.
/// </para>
///
/// <para>
/// Explicit integer values are pinned because the column is persisted
/// as <c>int</c>; renaming or reordering would silently change saved
/// rows' semantics.
/// </para>
/// </summary>
public enum StepCondition
{
    /// <summary>Default. Run iff no prior step has failed. Today's
    /// behaviour pre-M14 was effectively "always Success" because the
    /// orchestrator returned on first failure — same outcome.</summary>
    Success  = 0,

    /// <summary>Run iff a prior non-required step has failed. Used for
    /// cleanup / notification handlers that should fire when something
    /// went wrong upstream.</summary>
    Failure  = 1,

    /// <summary>Run regardless of prior outcomes.</summary>
    Always   = 2,

    /// <summary>Run iff
    /// <see cref="DeploymentStep.ConditionVariableExpression"/> evaluates
    /// truthy after Octostache expansion. See
    /// <c>AuditEventType.DeploymentVariableConditionUnresolved</c> for
    /// the failure mode when the expression references an unknown
    /// variable.</summary>
    Variable = 3,
}
