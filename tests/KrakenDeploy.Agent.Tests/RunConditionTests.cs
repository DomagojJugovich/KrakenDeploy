using FluentAssertions;
using KrakenDeploy.Execution;
using Octostache;

namespace KrakenDeploy.Agent.Tests;

/// <summary>
/// The offline orchestrate path evaluates run conditions through the SHARED
/// <see cref="StepConditionEvaluator"/> (KrakenDeploy.Execution) — the same
/// implementation the server orchestrator runs — so a process author gets
/// identical Run/Skip semantics online and offline. These tests pin the
/// int↔<see cref="StepCondition"/> mapping the agent applies when it casts
/// the contract's <c>int Condition</c> (Success=0, Failure=1, Always=2,
/// Variable=3) and the Variable truthiness contract, exercising the cast +
/// evaluator exactly as <c>DeploymentExecutor.RunStepInWaveAsync</c> does.
/// Routing the full decision matrix through that cast is the offline-vs-server
/// parity guard: both paths now resolve to the one shared evaluator, so these
/// cases can't drift from the online behaviour.
/// </summary>
public sealed class RunConditionTests
{
    /// <summary>Mirrors the agent's call site: cast the contract int to the
    /// pinned enum, build a VariableDictionary from the step's effective
    /// variables, and ask the shared evaluator.</summary>
    private static bool Runs(
        int condition, string? variableExpression, bool hasFailed,
        IReadOnlyDictionary<string, string>? variables = null)
    {
        var bag = variables?.ToVariableDictionary() ?? new VariableDictionary();
        var decision = StepConditionEvaluator.Evaluate(
            (StepCondition)condition, variableExpression, hasFailed, bag);
        return decision.Action == StepConditionEvaluator.Action.Run;
    }

    [Theory]
    [InlineData(0, false, true)]   // Success, no prior failure → run
    [InlineData(0, true, false)]   // Success, a prior failure → skip
    [InlineData(1, true, true)]    // Failure, a prior failure → run
    [InlineData(1, false, false)]  // Failure, no prior failure → skip
    [InlineData(2, false, true)]   // Always → run
    [InlineData(2, true, true)]    // Always → run even after failure
    public void Success_failure_always_conditions(int condition, bool hasFailed, bool expectedRun)
    {
        Runs(condition, variableExpression: null, hasFailed).Should().Be(expectedRun);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData("anything", false)]
    public void Variable_condition_truthiness(string flagValue, bool expectedRun)
    {
        var vars = new Dictionary<string, string> { ["Flag"] = flagValue };
        Runs(3, "#{Flag}", hasFailed: false, vars).Should().Be(expectedRun);
    }

    [Fact]
    public void Variable_condition_unresolved_token_is_falsy()
    {
        Runs(3, "#{Missing}", hasFailed: false).Should().BeFalse();
    }

    [Fact]
    public void Variable_condition_empty_expression_is_falsy()
    {
        Runs(3, variableExpression: null, hasFailed: false).Should().BeFalse();
    }
}
