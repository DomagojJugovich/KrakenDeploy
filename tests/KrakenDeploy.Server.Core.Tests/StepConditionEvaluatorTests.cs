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
        d.Kind.Should().Be(StepConditionEvaluator.Kind.Run);
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
            "#{NotSet} is missing — Octostache reports an error → Unresolved");
        d2.Kind.Should().Be(StepConditionEvaluator.Kind.Unresolved,
            "Octostache's out-error parameter is the authoritative signal " +
            "for missing variables — replaces the pre-M14.3.1 substring " +
            "heuristic on the result string");
    }

    [Fact]
    public void Variable_condition_distinguishes_Unresolved_from_falsy()
    {
        // M14.3.1 — the orchestrator routes Unresolved decisions to a
        // dedicated audit event type (DeploymentVariableConditionUnresolved).
        // Falsy-but-resolved (e.g. "#{Flag}" where Flag="false") routes to
        // DeploymentStepSkipped. Locking the Kind field prevents a future
        // contributor breaking the routing by changing Reason wording.
        var vars = new VariableDictionary();
        vars["Flag"] = "false";

        var falsy = StepConditionEvaluator.Evaluate(
            StepCondition.Variable, "#{Flag}", hasFailed: false, vars);
        falsy.Action.Should().Be(StepConditionEvaluator.Action.Skip);
        falsy.Kind.Should().Be(StepConditionEvaluator.Kind.Skipped,
            "resolved-but-falsy is Skipped, not Unresolved");

        var unresolved = StepConditionEvaluator.Evaluate(
            StepCondition.Variable, "#{MissingVar}", hasFailed: false, vars);
        unresolved.Action.Should().Be(StepConditionEvaluator.Action.Skip);
        unresolved.Kind.Should().Be(StepConditionEvaluator.Kind.Unresolved,
            "referenced variable absent — Octostache error → Unresolved");
    }

    [Fact]
    public void Variable_condition_with_literal_template_in_value_is_not_misclassified()
    {
        // M14.3.1 fix — pre-fix the evaluator did result.Contains("#{") to
        // flag unresolved. A resolved value that LEGITIMATELY contained
        // "#{" (e.g. a templated connection string operator copy-pasted
        // as a literal) would have been misclassified as Unresolved.
        // The new Octostache-error-driven detection is immune.
        var vars = new VariableDictionary();
        vars["WeirdLiteral"] = "true";
        vars["TemplateInside"] = "literal #{notathing} text"; // contains #{
        // The "WeirdLiteral" value is "true" — truthy.
        var d = StepConditionEvaluator.Evaluate(
            StepCondition.Variable, "#{WeirdLiteral}", hasFailed: false, vars);
        d.Action.Should().Be(StepConditionEvaluator.Action.Run);
        d.Kind.Should().Be(StepConditionEvaluator.Kind.Run);
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
