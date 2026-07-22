using KrakenDeploy.Execution;

namespace KrakenDeploy.Server.Core.Domain.Processes;

/// <summary>
/// Bundle of M14 step-execution knobs passed to service methods that
/// create or update a <see cref="ProcessStep"/>. Avoids inflating
/// the service signatures with seven additional parameters.
///
/// <para>
/// Defaults match the entity's defaults: <see cref="StepCondition.Success"/>,
/// <see cref="Required"/> = true, no retries, no timeout, sequential.
/// A caller that doesn't yet know about these knobs (e.g. an older
/// CLI command) can pass <see cref="Default"/> and get pre-M14 behaviour.
/// </para>
/// </summary>
public sealed record StepExecutionKnobs(
    StepCondition Condition = StepCondition.Success,
    string? ConditionVariableExpression = null,
    bool Required = true,
    int MaxRetries = 0,
    int RetryDelaySeconds = 0,
    int TimeoutSeconds = 0,
    StepStartTrigger StartTrigger = StepStartTrigger.StartAfterPrevious,
    // ── D3 control-flow flags (promoted from jsonb Config) ───────────────────
    // RunOnServer is a leaf/script flag; the other three are StepGroup-only.
    // They share the knobs bundle because it already threads through every
    // create/update path (ProcessService/RunbookService) for both owner kinds.
    // The ProcessValidator enforces the leaf-vs-group placement.
    bool RunOnServer = false,
    int? MaxParallelism = null,
    string? ForEachCollection = null,
    bool ForEachParallel = false)
{
    /// <summary>The default knobs — preserves pre-M14 behaviour exactly.</summary>
    public static readonly StepExecutionKnobs Default = new();

    /// <summary>Snapshots the knobs from an existing
    /// <see cref="ProcessStep"/>.</summary>
    public static StepExecutionKnobs From(ProcessStep step) => new(
        Condition:                   step.Condition,
        ConditionVariableExpression: step.ConditionVariableExpression,
        Required:                    step.Required,
        MaxRetries:                  step.MaxRetries,
        RetryDelaySeconds:           step.RetryDelaySeconds,
        TimeoutSeconds:              step.TimeoutSeconds,
        StartTrigger:                step.StartTrigger,
        RunOnServer:                 step.RunOnServer,
        MaxParallelism:              step.MaxParallelism,
        ForEachCollection:           step.ForEachCollection,
        ForEachParallel:             step.ForEachParallel);
}
