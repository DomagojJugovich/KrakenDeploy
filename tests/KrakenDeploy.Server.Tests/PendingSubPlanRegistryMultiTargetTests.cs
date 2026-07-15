using FluentAssertions;
using KrakenDeploy.Server.Transport;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// M-RollingDeployments Phase 1b — pins that the (deployment, target) slot
/// key keeps concurrent per-target sub-plans isolated. Two waves under the
/// same deployment id must not steal each other's TCS / step results when
/// they belong to different targets.
/// </summary>
public sealed class PendingSubPlanRegistryMultiTargetTests
{
    [Fact]
    public async Task Slots_for_two_targets_under_same_deployment_are_independent()
    {
        var registry = new PendingSubPlanRegistry();
        var deploymentId = Guid.NewGuid();
        var targetA = Guid.NewGuid();
        var targetB = Guid.NewGuid();
        var dispatchA = Guid.NewGuid();
        var dispatchB = Guid.NewGuid();

        var tcsA = new TaskCompletionSource<SubPlanResult>();
        var tcsB = new TaskCompletionSource<SubPlanResult>();
        registry.Register(deploymentId, targetA, dispatchA, tcsA);
        registry.Register(deploymentId, targetB, dispatchB, tcsB);

        registry.RecordStepResult(deploymentId, targetA, dispatchA, MakeResult(0, "A-step", true));
        registry.RecordStepResult(deploymentId, targetB, dispatchB, MakeResult(0, "B-step", false));

        registry.RouteCompletion(deploymentId, targetA, dispatchA, new SubPlanResult(true, null))
            .Should().Be(SubPlanCompletionRoute.ResolvedPending);

        var resolvedA = await tcsA.Task;
        resolvedA.Success.Should().BeTrue();
        tcsB.Task.IsCompleted.Should().BeFalse(
            because: "resolving target A's slot must not touch target B's TCS");

        var drainedA = registry.DrainStepResults(deploymentId, targetA);
        drainedA.Should().ContainSingle().Which.StepName.Should().Be("A-step");

        // B's bag remains intact until B drains.
        var drainedB = registry.DrainStepResults(deploymentId, targetB);
        drainedB.Should().ContainSingle().Which.StepName.Should().Be("B-step");
    }

    [Fact]
    public async Task Cancel_targets_only_the_specified_slot()
    {
        var registry = new PendingSubPlanRegistry();
        var deploymentId = Guid.NewGuid();
        var targetA = Guid.NewGuid();
        var targetB = Guid.NewGuid();

        var tcsA = new TaskCompletionSource<SubPlanResult>();
        var tcsB = new TaskCompletionSource<SubPlanResult>();
        registry.Register(deploymentId, targetA, Guid.NewGuid(), tcsA);
        registry.Register(deploymentId, targetB, Guid.NewGuid(), tcsB);

        registry.Cancel(deploymentId, targetA, "abort A");

        var resolvedA = await tcsA.Task;
        resolvedA.Success.Should().BeFalse();
        resolvedA.ErrorMessage.Should().Be("abort A");
        tcsB.Task.IsCompleted.Should().BeFalse(
            because: "cancelling target A's slot must not touch target B's TCS");

        registry.HasSlot(deploymentId, targetA).Should().BeFalse();
        registry.HasSlot(deploymentId, targetB).Should().BeTrue();
    }

    [Fact]
    public void RecordStepResult_for_target_without_a_slot_is_dropped()
    {
        // A's slot is open; B's is not. A late report addressed to B must
        // not leak into A's bag.
        var registry = new PendingSubPlanRegistry();
        var deploymentId = Guid.NewGuid();
        var targetA = Guid.NewGuid();
        var targetB = Guid.NewGuid();
        var dispatchA = Guid.NewGuid();

        registry.Register(deploymentId, targetA, dispatchA, new TaskCompletionSource<SubPlanResult>());

        registry.RecordStepResult(deploymentId, targetB, dispatchA, MakeResult(0, "ghost-B", true));

        registry.DrainStepResults(deploymentId, targetA).Should().BeEmpty();
        registry.DrainStepResults(deploymentId, targetB).Should().BeEmpty();
    }

    private static SubPlanStepResult MakeResult(int index, string name, bool success) =>
        new(StepIndex:    index,
            StepName:     name,
            Success:      success,
            ErrorMessage: success ? null : "boom",
            Outputs:      new Dictionary<string, string>());
}
