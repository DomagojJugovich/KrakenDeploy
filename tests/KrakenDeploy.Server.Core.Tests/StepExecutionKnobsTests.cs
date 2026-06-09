using FluentAssertions;
using KrakenDeploy.Execution;
using KrakenDeploy.Server.Core.Domain.Processes;

namespace KrakenDeploy.Server.Core.Tests;

/// <summary>
/// Unit tests pinning the M14.1 schema contract — defaults on the new
/// fields MUST preserve pre-M14 deployment semantics (Success Condition,
/// Required=true, no retries, no timeout, sequential). A future
/// contributor accidentally flipping a default would silently change
/// the runtime behaviour of every existing process.
/// </summary>
public sealed class StepExecutionKnobsTests
{
    [Fact]
    public void DeploymentStep_defaults_match_pre_M14_semantics()
    {
        var step = new DeploymentStep
        {
            Name      = "x",
            StepType  = "Kraken.Script",
            PackageId = "",
        };

        step.Condition.Should().Be(StepCondition.Success,
            "pre-M14 orchestrator stopped on first failure — equivalent to Success");
        step.ConditionVariableExpression.Should().BeNull();
        step.Required.Should().BeTrue(
            "pre-M14 every step was Required (any failure aborted)");
        step.MaxRetries.Should().Be(0);
        step.RetryDelaySeconds.Should().Be(0);
        step.TimeoutSeconds.Should().Be(0,
            "0 means unlimited — matches pre-M14 'no timeout' behaviour");
        step.StartTrigger.Should().Be(StepStartTrigger.StartAfterPrevious,
            "pre-M14 steps were strictly sequential");
    }

    [Fact]
    public void StepExecutionKnobs_Default_matches_DeploymentStep_defaults()
    {
        // The Default sentinel is what AddStepAsync uses when callers don't
        // pass knobs. It MUST match the entity's defaults or service-
        // created steps would behave differently from API-created ones.
        var knobs = StepExecutionKnobs.Default;
        var step = new DeploymentStep
        {
            Name      = "x",
            StepType  = "Kraken.Script",
            PackageId = "",
        };

        knobs.Condition.Should().Be(step.Condition);
        knobs.ConditionVariableExpression.Should().Be(step.ConditionVariableExpression);
        knobs.Required.Should().Be(step.Required);
        knobs.MaxRetries.Should().Be(step.MaxRetries);
        knobs.RetryDelaySeconds.Should().Be(step.RetryDelaySeconds);
        knobs.TimeoutSeconds.Should().Be(step.TimeoutSeconds);
        knobs.StartTrigger.Should().Be(step.StartTrigger);
    }

    [Fact]
    public void StepExecutionKnobs_From_DeploymentStep_round_trips()
    {
        var step = new DeploymentStep
        {
            Name                        = "x",
            StepType                    = "Kraken.Script",
            PackageId                   = "",
            Condition                   = StepCondition.Variable,
            ConditionVariableExpression = "#{ShouldRun}",
            Required                    = false,
            MaxRetries                  = 3,
            RetryDelaySeconds           = 30,
            TimeoutSeconds              = 600,
            StartTrigger                = StepStartTrigger.StartWithPrevious,
        };

        var knobs = StepExecutionKnobs.From(step);

        knobs.Condition.Should().Be(StepCondition.Variable);
        knobs.ConditionVariableExpression.Should().Be("#{ShouldRun}");
        knobs.Required.Should().BeFalse();
        knobs.MaxRetries.Should().Be(3);
        knobs.RetryDelaySeconds.Should().Be(30);
        knobs.TimeoutSeconds.Should().Be(600);
        knobs.StartTrigger.Should().Be(StepStartTrigger.StartWithPrevious);
    }

    [Theory]
    [InlineData(StepCondition.Success,  0)]
    [InlineData(StepCondition.Failure,  1)]
    [InlineData(StepCondition.Always,   2)]
    [InlineData(StepCondition.Variable, 3)]
    public void StepCondition_enum_values_are_pinned(StepCondition value, int expectedInt)
    {
        // The integer values are persisted in deployment_steps.condition
        // (and runbook equivalents in M-Runbooks). Renaming or reordering
        // the enum would silently re-map saved rows — pin the values here
        // so the bug is loud, not silent.
        ((int)value).Should().Be(expectedInt);
    }

    [Theory]
    [InlineData(StepStartTrigger.StartAfterPrevious, 0)]
    [InlineData(StepStartTrigger.StartWithPrevious,  1)]
    public void StepStartTrigger_enum_values_are_pinned(StepStartTrigger value, int expectedInt)
    {
        ((int)value).Should().Be(expectedInt);
    }
}
