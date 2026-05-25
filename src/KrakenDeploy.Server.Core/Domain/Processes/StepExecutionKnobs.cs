namespace KrakenDeploy.Server.Core.Domain.Processes;

/// <summary>
/// Bundle of M14 step-execution knobs passed to service methods that
/// create or update a <see cref="DeploymentStep"/>. Avoids inflating
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
    StepStartTrigger StartTrigger = StepStartTrigger.StartAfterPrevious)
{
    /// <summary>The default knobs — preserves pre-M14 behaviour exactly.</summary>
    public static readonly StepExecutionKnobs Default = new();

    /// <summary>Snapshots the knobs from an existing
    /// <see cref="DeploymentStep"/>.</summary>
    public static StepExecutionKnobs From(DeploymentStep step) => new(
        Condition:                   step.Condition,
        ConditionVariableExpression: step.ConditionVariableExpression,
        Required:                    step.Required,
        MaxRetries:                  step.MaxRetries,
        RetryDelaySeconds:           step.RetryDelaySeconds,
        TimeoutSeconds:              step.TimeoutSeconds,
        StartTrigger:                step.StartTrigger);
}
