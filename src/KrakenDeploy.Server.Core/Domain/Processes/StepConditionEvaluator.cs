using Octostache;

namespace KrakenDeploy.Server.Core.Domain.Processes;

/// <summary>
/// Pure-function helper that decides whether a step should run given
/// its Run Condition + the deployment's <c>hasFailed</c> state + the
/// variable bag (for <see cref="StepCondition.Variable"/>).
///
/// <para>
/// Extracted from the orchestrator so it can be unit-tested without
/// spinning up the full <c>DeploymentWorker</c> machinery. The
/// orchestrator calls this once per step before dispatching execution
/// and routes Skip decisions to the audit log + deployment log without
/// changing the failed-state flag.
/// </para>
/// </summary>
public static class StepConditionEvaluator
{
    public enum Action
    {
        /// <summary>Run the step.</summary>
        Run,

        /// <summary>Skip the step. Does NOT mark the deployment as failed.</summary>
        Skip,
    }

    /// <summary>
    /// Result of evaluating one step's condition. <see cref="Reason"/>
    /// is human-readable and used verbatim in the deployment-log line +
    /// audit row's Details so an operator can see WHY a step was skipped
    /// or ran.
    /// </summary>
    public sealed record Decision(Action Action, string Reason);

    /// <summary>
    /// Evaluates whether the step runs.
    /// </summary>
    /// <param name="condition">The step's Run Condition.</param>
    /// <param name="variableExpression">The Octostache expression for
    /// <see cref="StepCondition.Variable"/>. Ignored for other conditions.</param>
    /// <param name="hasFailed">True when at least one prior non-required
    /// step has failed.</param>
    /// <param name="variables">The current variable dictionary used to
    /// evaluate the Variable-condition expression. Must include the same
    /// values the step would see if it ran (Octopus + project + tenant +
    /// release variables).</param>
    public static Decision Evaluate(
        StepCondition condition,
        string? variableExpression,
        bool hasFailed,
        VariableDictionary variables)
    {
        ArgumentNullException.ThrowIfNull(variables);

        return condition switch
        {
            StepCondition.Success when hasFailed =>
                new(Action.Skip,
                    "Condition=Success but a prior step has failed; skipping."),

            StepCondition.Success =>
                new(Action.Run,
                    "Condition=Success and no prior failure."),

            StepCondition.Failure when hasFailed =>
                new(Action.Run,
                    "Condition=Failure and a prior step failed."),

            StepCondition.Failure =>
                new(Action.Skip,
                    "Condition=Failure but no prior step has failed; skipping."),

            StepCondition.Always =>
                new(Action.Run,
                    "Condition=Always."),

            StepCondition.Variable =>
                EvaluateVariable(variableExpression, variables),

            _ => new(Action.Skip,
                    $"Unknown Condition value {condition}; skipping defensively."),
        };
    }

    /// <summary>
    /// Variable-condition truthy contract (decided 2026-05-25):
    /// case-insensitive <c>"true"</c> or the literal <c>"1"</c>. Everything
    /// else — empty string, <c>"false"</c>, <c>"0"</c>, unresolved expression
    /// referencing an undefined variable — is falsy.
    /// </summary>
    private static Decision EvaluateVariable(
        string? expression, VariableDictionary variables)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return new(Action.Skip,
                "Condition=Variable with empty expression; treated as falsy.");
        }

        string? result;
        try
        {
            result = variables.Evaluate(expression);
        }
        catch (Exception ex)
        {
            return new(Action.Skip,
                $"Condition=Variable expression failed to evaluate ({ex.GetType().Name}): {expression}");
        }

        if (result is null)
        {
            return new(Action.Skip,
                $"Condition=Variable expression unresolved (referenced variable missing): {expression}");
        }

        // Octostache leaves the literal #{...} in place when a referenced
        // variable doesn't exist (rather than returning null). Treat any
        // remaining template syntax as "unresolved" so the audit row has
        // the right event type and operators can filter for it.
        if (result.Contains("#{", StringComparison.Ordinal))
        {
            return new(Action.Skip,
                $"Condition=Variable expression unresolved (template tokens remain after expansion): {expression}");
        }

        var trimmed = result.Trim();
        var truthy = string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase)
            || trimmed == "1";

        return truthy
            ? new(Action.Run,  $"Condition=Variable truthy: {expression} = {trimmed}")
            : new(Action.Skip, $"Condition=Variable falsy: {expression} = {trimmed}");
    }
}
