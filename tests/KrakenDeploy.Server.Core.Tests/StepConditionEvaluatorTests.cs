using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Processes;
using Octostache;

namespace KrakenDeploy.Server.Core.Tests;

/// <summary>
/// Unit tests for the pure M14.2 condition gate. No orchestrator or
/// DB — exercises the decision tree against synthetic VariableDictionary
/// values. The orchestrator's loop logic is exercised separately via
/// integration tests; this file pins the per-call contract.
/// </summary>
public sealed class StepConditionEvaluatorTests
{
    private static VariableDictionary EmptyVars() => new();

    [Fact]
    public void Success_runs_when_no_prior_failure()
    {
        var d = StepConditionEvaluator.Evaluate(
            StepCondition.Success, null, hasFailed: false, EmptyVars());
        d.Action.Should().Be(StepConditionEvaluator.Action.Run);
        d.Reason.Should().Contain("no prior failure");
    }

    [Fact]
    public void Success_skips_when_prior_failure()
    {
        var d = StepConditionEvaluator.Evaluate(
            StepCondition.Success, null, hasFailed: true, EmptyVars());
        d.Action.Should().Be(StepConditionEvaluator.Action.Skip);
        d.Reason.Should().Contain("prior step has failed");
    }

    [Fact]
    public void Failure_runs_when_prior_failure()
    {
        // Cleanup / notification handler: fires only when an upstream
        // non-required step has failed. Pin this contract — without it
        // operators can't trigger rollback steps automatically.
        var d = StepConditionEvaluator.Evaluate(
            StepCondition.Failure, null, hasFailed: true, EmptyVars());
        d.Action.Should().Be(StepConditionEvaluator.Action.Run);
        d.Reason.Should().Contain("prior step failed");
    }

    [Fact]
    public void Failure_skips_in_clean_deployment()
    {
        var d = StepConditionEvaluator.Evaluate(
            StepCondition.Failure, null, hasFailed: false, EmptyVars());
        d.Action.Should().Be(StepConditionEvaluator.Action.Skip);
        d.Reason.Should().Contain("no prior step has failed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Always_always_runs(bool hasFailed)
    {
        var d = StepConditionEvaluator.Evaluate(
            StepCondition.Always, null, hasFailed, EmptyVars());
        d.Action.Should().Be(StepConditionEvaluator.Action.Run);
    }

    // ── Variable condition ────────────────────────────────────────────────

    [Theory]
    [InlineData("true",  true)]
    [InlineData("True",  true)]
    [InlineData("TRUE",  true)]
    [InlineData("1",     true)]
    [InlineData(" true ", true)]   // trimmed
    [InlineData("false", false)]
    [InlineData("0",     false)]
    [InlineData("",      false)]
    [InlineData("yes",   false)]   // only "true"/"1" are truthy — pinned
    [InlineData("nope",  false)]
    public void Variable_condition_honours_truthy_contract(string literal, bool expectedRun)
    {
        var d = StepConditionEvaluator.Evaluate(
            StepCondition.Variable, literal, hasFailed: false, EmptyVars());
        d.Action.Should().Be(expectedRun
            ? StepConditionEvaluator.Action.Run
            : StepConditionEvaluator.Action.Skip);
    }

    [Fact]
    public void Variable_condition_evaluates_Octostache_expression()
    {
        var vars = new VariableDictionary();
        vars["Octopus.Environment.Name"] = "Production";
        vars["ShouldRun"]                = "true";

        var truthyExpr = "#{ShouldRun}";
        var d = StepConditionEvaluator.Evaluate(
            StepCondition.Variable, truthyExpr, hasFailed: false, vars);
        d.Action.Should().Be(StepConditionEvaluator.Action.Run,
            "#{ShouldRun} resolves to 'true'");

        var falsyExpr = "#{NotSet}";
        var d2 = StepConditionEvaluator.Evaluate(
            StepCondition.Variable, falsyExpr, hasFailed: false, vars);
        d2.Action.Should().Be(StepConditionEvaluator.Action.Skip,
            "#{NotSet} resolves to null (unresolved) → falsy");
        d2.Reason.Should().Contain("unresolved");
    }

    [Fact]
    public void Variable_condition_empty_expression_is_falsy()
    {
        var d = StepConditionEvaluator.Evaluate(
            StepCondition.Variable, "", hasFailed: false, EmptyVars());
        d.Action.Should().Be(StepConditionEvaluator.Action.Skip);
        d.Reason.Should().Contain("empty");

        var d2 = StepConditionEvaluator.Evaluate(
            StepCondition.Variable, null, hasFailed: false, EmptyVars());
        d2.Action.Should().Be(StepConditionEvaluator.Action.Skip,
            "null expression is the same as empty");
    }

    [Fact]
    public void Throws_when_variables_is_null()
    {
        var act = () => StepConditionEvaluator.Evaluate(
            StepCondition.Always, null, hasFailed: false, variables: null!);
        act.Should().Throw<ArgumentNullException>(
            "the orchestrator always has a variable bag — passing null is a bug");
    }
}
