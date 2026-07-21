using KrakenDeploy.Server.Core.Domain.Audit;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// Per-kind audit-event vocabulary for the unified orchestrator (D1 engine
/// merge). Maps each orchestration event to the kind-appropriate
/// <see cref="AuditEventType"/> constant plus the audit <c>subjectType</c>, so a
/// runbook run executing through <see cref="DeploymentWorker"/> emits
/// <c>RunbookRun.*</c> events (never <c>Deployment.*</c>). This keeps
/// <c>Deployment.*</c> and <c>RunbookRun.*</c> wildcard subscriptions correct —
/// <c>SubscriptionMatcher</c> matches on the event-type string prefix, so
/// reusing a <c>Deployment.*</c> name for a runbook run would leak into every
/// <c>Deployment.*</c> subscription.
/// <para>
/// The event names are ADDITIVE — <c>Deployment.*</c> constants are never reused
/// for runbook runs and never renamed.
/// </para>
/// </summary>
internal sealed record TaskAuditVocabulary(
    string SubjectType,
    string ForEachEmpty,
    string ForEachUnresolved,
    string MixedWaveRefused,
    string RequiredStepFailed,
    string StepRetried,
    string StepSkipped,
    string VariableConditionUnresolved,
    string StepTimedOut,
    string StepFailedNonRequired,
    string Slow,
    string TargetSlow,
    string StepSlow,
    string TargetDropped,
    string ParallelOutputCollision,
    string RollingBatchStarted,
    string RollingBatchCompleted)
{
    /// <summary>Vocabulary for <see cref="Core.Domain.Deployments.Deployment"/> tasks.</summary>
    public static readonly TaskAuditVocabulary Deployment = new(
        SubjectType:                 "Deployment",
        ForEachEmpty:                AuditEventType.DeploymentForEachEmpty,
        ForEachUnresolved:           AuditEventType.DeploymentForEachUnresolved,
        MixedWaveRefused:            AuditEventType.DeploymentMixedWaveRefused,
        RequiredStepFailed:          AuditEventType.DeploymentRequiredStepFailed,
        StepRetried:                 AuditEventType.DeploymentStepRetried,
        StepSkipped:                 AuditEventType.DeploymentStepSkipped,
        VariableConditionUnresolved: AuditEventType.DeploymentVariableConditionUnresolved,
        StepTimedOut:                AuditEventType.DeploymentStepTimedOut,
        StepFailedNonRequired:       AuditEventType.DeploymentStepFailedNonRequired,
        Slow:                        AuditEventType.DeploymentSlow,
        TargetSlow:                  AuditEventType.DeploymentTargetSlow,
        StepSlow:                    AuditEventType.DeploymentStepSlow,
        TargetDropped:               AuditEventType.DeploymentTargetDropped,
        ParallelOutputCollision:     AuditEventType.DeploymentParallelOutputCollision,
        RollingBatchStarted:         AuditEventType.DeploymentRollingBatchStarted,
        RollingBatchCompleted:       AuditEventType.DeploymentRollingBatchCompleted);

    /// <summary>Vocabulary for <see cref="Core.Domain.Runbooks.RunbookRun"/> tasks.</summary>
    public static readonly TaskAuditVocabulary RunbookRun = new(
        SubjectType:                 "RunbookRun",
        ForEachEmpty:                AuditEventType.RunbookRunForEachEmpty,
        ForEachUnresolved:           AuditEventType.RunbookRunForEachUnresolved,
        MixedWaveRefused:            AuditEventType.RunbookRunMixedWaveRefused,
        RequiredStepFailed:          AuditEventType.RunbookRunRequiredStepFailed,
        StepRetried:                 AuditEventType.RunbookRunStepRetried,
        StepSkipped:                 AuditEventType.RunbookRunStepSkipped,
        VariableConditionUnresolved: AuditEventType.RunbookRunVariableConditionUnresolved,
        StepTimedOut:                AuditEventType.RunbookRunStepTimedOut,
        StepFailedNonRequired:       AuditEventType.RunbookRunStepFailedNonRequired,
        Slow:                        AuditEventType.RunbookRunSlow,
        TargetSlow:                  AuditEventType.RunbookRunTargetSlow,
        StepSlow:                    AuditEventType.RunbookRunStepSlow,
        TargetDropped:               AuditEventType.RunbookRunTargetDropped,
        ParallelOutputCollision:     AuditEventType.RunbookRunParallelOutputCollision,
        RollingBatchStarted:         AuditEventType.RunbookRunRollingBatchStarted,
        RollingBatchCompleted:       AuditEventType.RunbookRunRollingBatchCompleted);
}
