using FluentAssertions;
using KrakenDeploy.Server.Transport;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// F2 — <see cref="PendingSubPlanRegistry.TryMarkExecutionStarted"/>, the hook the
/// orchestrator re-arms its wave deadline from. Matching is deliberately STRICTER
/// than <see cref="PendingSubPlanRegistry.RouteCompletion"/>'s: only the exact
/// attempt currently awaited may move a deadline, so a superseded attempt's late
/// report (flushed from the agent's at-least-once outbox) cannot hand the LIVE
/// attempt extra time.
/// </summary>
public sealed class PendingSubPlanRegistryExecutionStartedTests
{
    private static readonly Guid DeploymentId = Guid.NewGuid();
    private static readonly Guid TargetId = Guid.NewGuid();

    [Fact]
    public void Matching_dispatch_arms_once_and_invokes_the_callback()
    {
        var registry = new PendingSubPlanRegistry();
        var dispatchId = Guid.NewGuid();
        var armed = 0;
        registry.Register(
            DeploymentId, TargetId, dispatchId, new TaskCompletionSource<SubPlanResult>(),
            onExecutionStarted: () => armed++);

        registry.TryMarkExecutionStarted(DeploymentId, TargetId, dispatchId)
            .Should().BeTrue();
        armed.Should().Be(1);

        // The outbox is at-least-once: a redelivery must not re-arm (which would
        // silently extend the deadline every time it arrived).
        registry.TryMarkExecutionStarted(DeploymentId, TargetId, dispatchId)
            .Should().BeFalse();
        armed.Should().Be(1);
    }

    [Fact]
    public void Report_from_a_different_attempt_does_not_arm_the_live_attempt()
    {
        var registry = new PendingSubPlanRegistry();
        var liveDispatch = Guid.NewGuid();
        var armed = 0;
        registry.Register(
            DeploymentId, TargetId, liveDispatch, new TaskCompletionSource<SubPlanResult>(),
            onExecutionStarted: () => armed++);

        registry.TryMarkExecutionStarted(DeploymentId, TargetId, Guid.NewGuid())
            .Should().BeFalse("a superseded attempt must not extend the live attempt's deadline");
        armed.Should().Be(0);
    }

    [Fact]
    public void Empty_dispatch_id_never_arms()
    {
        // Guid.Empty is the shared "no key" marker (offline bundles, legacy plans).
        // RouteCompletion accepts it as a wildcard; arming must not — a wildcard
        // here would let any keyless report move an unrelated attempt's deadline.
        var registry = new PendingSubPlanRegistry();
        var armed = 0;
        registry.Register(
            DeploymentId, TargetId, Guid.NewGuid(), new TaskCompletionSource<SubPlanResult>(),
            onExecutionStarted: () => armed++);

        registry.TryMarkExecutionStarted(DeploymentId, TargetId, Guid.Empty)
            .Should().BeFalse();
        armed.Should().Be(0);
    }

    [Fact]
    public void Report_with_no_open_slot_is_a_noop()
    {
        var registry = new PendingSubPlanRegistry();

        registry.TryMarkExecutionStarted(DeploymentId, TargetId, Guid.NewGuid())
            .Should().BeFalse("post-restart / unknown task — nothing to arm");
    }

    [Fact]
    public void Report_for_another_target_does_not_arm_this_targets_slot()
    {
        // The hub keys the lookup on the CONNECTION's claimed target id, so this is
        // the trust boundary: a foreign agent probes its own (empty) slot.
        var registry = new PendingSubPlanRegistry();
        var dispatchId = Guid.NewGuid();
        var armed = 0;
        registry.Register(
            DeploymentId, TargetId, dispatchId, new TaskCompletionSource<SubPlanResult>(),
            onExecutionStarted: () => armed++);

        registry.TryMarkExecutionStarted(DeploymentId, Guid.NewGuid(), dispatchId)
            .Should().BeFalse();
        armed.Should().Be(0);
    }

    [Fact]
    public void Report_after_the_attempt_ended_is_a_noop()
    {
        var registry = new PendingSubPlanRegistry();
        var dispatchId = Guid.NewGuid();
        var armed = 0;
        registry.Register(
            DeploymentId, TargetId, dispatchId, new TaskCompletionSource<SubPlanResult>(),
            onExecutionStarted: () => armed++);

        registry.RouteCompletion(DeploymentId, TargetId, dispatchId, new SubPlanResult(true, null))
            .Should().Be(SubPlanCompletionRoute.ResolvedPending);

        registry.TryMarkExecutionStarted(DeploymentId, TargetId, dispatchId)
            .Should().BeFalse("the slot is gone — the callback would touch a disposed timer");
        armed.Should().Be(0);
    }

    [Fact]
    public void Re_registering_a_new_attempt_resets_the_arm_state()
    {
        // A wave retry registers a fresh attempt under the same (deployment, target)
        // slot key. It must be armable in its own right.
        var registry = new PendingSubPlanRegistry();
        var firstDispatch = Guid.NewGuid();
        var secondDispatch = Guid.NewGuid();
        var firstArmed = 0;
        var secondArmed = 0;

        registry.Register(
            DeploymentId, TargetId, firstDispatch, new TaskCompletionSource<SubPlanResult>(),
            onExecutionStarted: () => firstArmed++);
        registry.TryMarkExecutionStarted(DeploymentId, TargetId, firstDispatch).Should().BeTrue();

        registry.Register(
            DeploymentId, TargetId, secondDispatch, new TaskCompletionSource<SubPlanResult>(),
            onExecutionStarted: () => secondArmed++);
        registry.TryMarkExecutionStarted(DeploymentId, TargetId, secondDispatch).Should().BeTrue();

        firstArmed.Should().Be(1);
        secondArmed.Should().Be(1);

        registry.TryMarkExecutionStarted(DeploymentId, TargetId, firstDispatch)
            .Should().BeFalse("the first attempt is no longer the awaited one");
        firstArmed.Should().Be(1);
    }
}
