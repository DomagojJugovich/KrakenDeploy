using FluentAssertions;
using KrakenDeploy.Server.Transport;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// Unit tests for the M14.4 extensions on <see cref="PendingSubPlanRegistry"/>:
/// the per-step results bag that the worker drains after a wave's
/// sub-plan completion to apply per-step Required attribution + collision
/// audits. Pin: late reports (no in-flight sub-plan) are dropped, Register
/// clears the previous wave's bag, Drain returns arrival-order.
///
/// <para>
/// M-RollingDeployments Phase 1b: the registry's slot key widened to
/// <c>(deploymentId, targetId)</c>. These tests use a single canonical
/// <see cref="TargetId"/> so the M14.4 semantics still hold for the
/// single-target dispatch path; <see cref="PendingSubPlanRegistryMultiTargetTests"/>
/// covers the multi-target slot isolation.
/// </para>
/// </summary>
public sealed class PendingSubPlanRegistryStepResultsTests
{
    private static readonly Guid TargetId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DispatchId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void RecordStepResult_without_Register_is_dropped()
    {
        // A "late" agent report for a wave that already resolved must not
        // accumulate state — otherwise long-running deployments would leak
        // memory through the registry.
        var registry = new PendingSubPlanRegistry();
        var deploymentId = Guid.NewGuid();

        registry.RecordStepResult(deploymentId, TargetId, DispatchId, MakeResult(0, "ghost", true));

        registry.DrainStepResults(deploymentId, TargetId).Should().BeEmpty();
    }

    [Fact]
    public void Register_clears_prior_wave_per_step_results()
    {
        var registry = new PendingSubPlanRegistry();
        var deploymentId = Guid.NewGuid();

        // First wave: register, record one result, never drain.
        var firstTcs = new TaskCompletionSource<SubPlanResult>();
        registry.Register(deploymentId, TargetId, DispatchId, firstTcs);
        registry.RecordStepResult(deploymentId, TargetId, DispatchId, MakeResult(0, "first", true));

        // Second wave: re-register. The bag should be clean even though
        // the first wave's result was never drained.
        var secondTcs = new TaskCompletionSource<SubPlanResult>();
        registry.Register(deploymentId, TargetId, DispatchId, secondTcs);

        registry.DrainStepResults(deploymentId, TargetId).Should().BeEmpty();
    }

    [Fact]
    public void Drain_returns_arrival_order()
    {
        var registry = new PendingSubPlanRegistry();
        var deploymentId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<SubPlanResult>();
        registry.Register(deploymentId, TargetId, DispatchId, tcs);

        registry.RecordStepResult(deploymentId, TargetId, DispatchId, MakeResult(2, "C", true));
        registry.RecordStepResult(deploymentId, TargetId, DispatchId, MakeResult(0, "A", false));
        registry.RecordStepResult(deploymentId, TargetId, DispatchId, MakeResult(1, "B", true));

        var drained = registry.DrainStepResults(deploymentId, TargetId);

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
        registry.Register(deploymentId, TargetId, DispatchId, tcs);

        registry.RecordStepResult(deploymentId, TargetId, DispatchId, MakeResult(0, "A", true));

        registry.DrainStepResults(deploymentId, TargetId).Should().HaveCount(1);
        registry.DrainStepResults(deploymentId, TargetId).Should().BeEmpty();
    }

    [Fact]
    public void RouteCompletion_then_Drain_returns_the_accumulated_results()
    {
        // The orchestrator's typical happy path: Register → agent sends
        // per-step + final → RouteCompletion fires the TCS → Drain returns
        // the wave's per-step reports for the Required + collision pass.
        var registry = new PendingSubPlanRegistry();
        var deploymentId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<SubPlanResult>();
        registry.Register(deploymentId, TargetId, DispatchId, tcs);

        registry.RecordStepResult(deploymentId, TargetId, DispatchId, MakeResult(0, "A", true));
        registry.RecordStepResult(deploymentId, TargetId, DispatchId, MakeResult(1, "B", false));

        registry.RouteCompletion(deploymentId, TargetId, DispatchId, new SubPlanResult(false, "B failed"))
            .Should().Be(SubPlanCompletionRoute.ResolvedPending);

        var drained = registry.DrainStepResults(deploymentId, TargetId);
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
