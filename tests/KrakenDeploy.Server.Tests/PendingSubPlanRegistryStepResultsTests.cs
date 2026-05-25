using FluentAssertions;
using KrakenDeploy.Server.Transport;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// Unit tests for the M14.4 extensions on <see cref="PendingSubPlanRegistry"/>:
/// the per-step results bag that the worker drains after a wave's
/// sub-plan completion to apply per-step Required attribution + collision
/// audits. Pin: late reports (no in-flight sub-plan) are dropped, Register
/// clears the previous wave's bag, Drain returns arrival-order.
/// </summary>
public sealed class PendingSubPlanRegistryStepResultsTests
{
    [Fact]
    public void RecordStepResult_without_Register_is_dropped()
    {
        // A "late" agent report for a wave that already resolved must not
        // accumulate state — otherwise long-running deployments would leak
        // memory through the registry.
        var registry = new PendingSubPlanRegistry();
        var deploymentId = Guid.NewGuid();

        registry.RecordStepResult(deploymentId, MakeResult(0, "ghost", true));

        registry.DrainStepResults(deploymentId).Should().BeEmpty();
    }

    [Fact]
    public void Register_clears_prior_wave_per_step_results()
    {
        var registry = new PendingSubPlanRegistry();
        var deploymentId = Guid.NewGuid();

        // First wave: register, record one result, never drain.
        var firstTcs = new TaskCompletionSource<SubPlanResult>();
        registry.Register(deploymentId, firstTcs);
        registry.RecordStepResult(deploymentId, MakeResult(0, "first", true));

        // Second wave: re-register. The bag should be clean even though
        // the first wave's result was never drained.
        var secondTcs = new TaskCompletionSource<SubPlanResult>();
        registry.Register(deploymentId, secondTcs);

        registry.DrainStepResults(deploymentId).Should().BeEmpty();
    }

    [Fact]
    public void Drain_returns_arrival_order()
    {
        var registry = new PendingSubPlanRegistry();
        var deploymentId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<SubPlanResult>();
        registry.Register(deploymentId, tcs);

        registry.RecordStepResult(deploymentId, MakeResult(2, "C", true));
        registry.RecordStepResult(deploymentId, MakeResult(0, "A", false));
        registry.RecordStepResult(deploymentId, MakeResult(1, "B", true));

        var drained = registry.DrainStepResults(deploymentId);

        // Arrival order — not StepIndex order. The orchestrator does its
        // own ordering (by StepIndex) where it needs SortOrder semantics
        // (collision detection); raw drain returns what the agent sent.
        drained.Select(r => r.StepName).Should().Equal(["C", "A", "B"]);
    }

    [Fact]
    public void Drain_clears_the_bag_so_a_second_call_yields_empty()
    {
        var registry = new PendingSubPlanRegistry();
        var deploymentId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<SubPlanResult>();
        registry.Register(deploymentId, tcs);

        registry.RecordStepResult(deploymentId, MakeResult(0, "A", true));

        registry.DrainStepResults(deploymentId).Should().HaveCount(1);
        registry.DrainStepResults(deploymentId).Should().BeEmpty();
    }

    [Fact]
    public void TryResolve_then_Drain_returns_the_accumulated_results()
    {
        // The orchestrator's typical happy path: Register → agent sends
        // per-step + final → TryResolve fires the TCS → Drain returns
        // the wave's per-step reports for the Required + collision pass.
        var registry = new PendingSubPlanRegistry();
        var deploymentId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<SubPlanResult>();
        registry.Register(deploymentId, tcs);

        registry.RecordStepResult(deploymentId, MakeResult(0, "A", true));
        registry.RecordStepResult(deploymentId, MakeResult(1, "B", false));

        registry.TryResolve(deploymentId, new SubPlanResult(false, "B failed"))
            .Should().BeTrue();

        var drained = registry.DrainStepResults(deploymentId);
        drained.Should().HaveCount(2);
        drained[1].Success.Should().BeFalse();
    }

    private static SubPlanStepResult MakeResult(int index, string name, bool success) =>
        new(StepIndex:    index,
            StepName:     name,
            Success:      success,
            ErrorMessage: success ? null : "boom",
            Outputs:      new Dictionary<string, string>());
}
