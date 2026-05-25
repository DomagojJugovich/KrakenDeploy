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
    /// Categorical reason for the decision. Lets the caller (orchestrator)
    /// switch on a typed value rather than parsing <see cref="Decision.Reason"/>
    /// text — keeps audit-event-type discrimination decoupled from the
    /// human-readable message wording.
    /// </summary>
    public enum Kind
    {
        /// <summary>Step runs because its condition matched.</summary>
        Run,

        /// <summary>Step is skipped because Run Condition didn't match
        /// (Success after a failure, Failure with no failure, Variable
        /// evaluated falsy, Always — last one doesn't apply since Always
        /// always runs).</summary>
        Skipped,

        /// <summary>Variable-condition expression referenced a missing
        /// variable or failed to parse — surfaces as a dedicated audit
        /// event type so operators can filter for it.</summary>
        Unresolved,
    }

    /// <summary>
    /// Result of evaluating one step's condition. <see cref="Reason"/>
    /// is human-readable and used verbatim in the deployment-log line +
    /// audit row's Details so an operator can see WHY a step was skipped
    /// or ran. <see cref="Kind"/> is the machine-readable category the
    /// orchestrator switches on when picking the audit event type.
    /// </summary>
    public sealed record Decision(Action Action, Kind Kind, string Reason);

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
                new(Action.Skip, Kind.Skipped,
                    "Condition=Success but a prior step has failed; skipping."),

            StepCondition.Success =>
                new(Action.Run, Kind.Run,
                    "Condition=Success and no prior failure."),

            StepCondition.Failure when hasFailed =>
                new(Action.Run, Kind.Run,
                    "Condition=Failure and a prior step failed."),

            StepCondition.Failure =>
                new(Action.Skip, Kind.Skipped,
                    "Condition=Failure but no prior step has failed; skipping."),

            StepCondition.Always =>
                new(Action.Run, Kind.Run,
                    "Condition=Always."),

            StepCondition.Variable =>
                EvaluateVariable(variableExpression, variables),

            _ => new(Action.Skip, Kind.Skipped,
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
            return new(Action.Skip, Kind.Skipped,
                "Condition=Variable with empty expression; treated as falsy.");
        }

        string result;
        string? error;
        try
        {
            // Octostache's three-arg overload reports unresolved-token + parse
            // errors through the out string. haltOnError:false means the
            // returned string is the partially-evaluated template (with
            // #{...} preserved for missing tokens). We treat any non-empty
            // error as Unresolved → falsy + dedicated audit event type.
            result = variables.Evaluate(expression, out error, haltOnError: false)
                ?? string.Empty;
        }
        catch (Exception ex)
        {
            return new(Action.Skip, Kind.Unresolved,
                $"Condition=Variable expression failed to evaluate ({ex.GetType().Name}): {expression}");
        }

        if (!string.IsNullOrEmpty(error))
        {
            return new(Action.Skip, Kind.Unresolved,
                $"Condition=Variable expression unresolved: {expression}. Octostache reported: {error}");
        }

        var trimmed = result.Trim();
        var truthy = string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase)
            || trimmed == "1";

        return truthy
            ? new(Action.Run,  Kind.Run,
                $"Condition=Variable truthy: {expression} = {trimmed}")
            : new(Action.Skip, Kind.Skipped,
                $"Condition=Variable falsy: {expression} = {trimmed}");
    }
}
