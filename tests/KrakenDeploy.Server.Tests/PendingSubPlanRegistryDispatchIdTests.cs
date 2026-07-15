using FluentAssertions;
using KrakenDeploy.Server.Transport;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// B2 (B6.2 pulled forward) — per-dispatch idempotency in
/// <see cref="PendingSubPlanRegistry"/>. The agent's at-least-once report
/// outbox may deliver a completion twice (ack lost in a disconnect) or late
/// (after the wave was cancelled and re-dispatched). A duplicate or stale
/// completion must be swallowed (<see cref="SubPlanCompletionRoute.StaleOrDuplicate"/>)
/// — it must neither resolve a DIFFERENT attempt's TCS nor fall through to
/// the hub's DB fallback finalizer, which would finalize a mid-flight
/// deployment.
/// </summary>
public sealed class PendingSubPlanRegistryDispatchIdTests
{
    private static readonly Guid DeploymentId = Guid.NewGuid();
    private static readonly Guid TargetId = Guid.NewGuid();

    private static SubPlanResult Ok() => new(true, null);

    [Fact]
    public async Task Matching_dispatch_resolves_the_pending_slot()
    {
        var registry = new PendingSubPlanRegistry();
        var dispatchId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<SubPlanResult>();
        registry.Register(DeploymentId, TargetId, dispatchId, tcs);

        registry.RouteCompletion(DeploymentId, TargetId, dispatchId, new SubPlanResult(false, "boom"))
            .Should().Be(SubPlanCompletionRoute.ResolvedPending);

        (await tcs.Task).ErrorMessage.Should().Be("boom");
    }

    [Fact]
    public void Duplicate_completion_after_resolve_is_swallowed()
    {
        // At-least-once delivery: the first copy resolved the wave; the second
        // must not reach the DB fallback (the deployment may still be mid-flight
        // in a later wave).
        var registry = new PendingSubPlanRegistry();
        var dispatchId = Guid.NewGuid();
        registry.Register(DeploymentId, TargetId, dispatchId, new TaskCompletionSource<SubPlanResult>());

        registry.RouteCompletion(DeploymentId, TargetId, dispatchId, Ok())
            .Should().Be(SubPlanCompletionRoute.ResolvedPending);
        registry.RouteCompletion(DeploymentId, TargetId, dispatchId, Ok())
            .Should().Be(SubPlanCompletionRoute.StaleOrDuplicate);
    }

    [Fact]
    public async Task Stale_completion_cannot_resolve_a_newer_attempt()
    {
        // Wave retry: attempt 1 timed out (cancelled), attempt 2 registered a
        // fresh slot under the SAME (deployment, target) key. A buffered
        // completion from attempt 1 flushing after reconnect must not resolve
        // attempt 2's TCS.
        var registry = new PendingSubPlanRegistry();
        var attempt1 = Guid.NewGuid();
        var attempt2 = Guid.NewGuid();

        registry.Register(DeploymentId, TargetId, attempt1, new TaskCompletionSource<SubPlanResult>());
        registry.Cancel(DeploymentId, TargetId, "wave timed out");

        var tcs2 = new TaskCompletionSource<SubPlanResult>();
        registry.Register(DeploymentId, TargetId, attempt2, tcs2);

        registry.RouteCompletion(DeploymentId, TargetId, attempt1, Ok())
            .Should().Be(SubPlanCompletionRoute.StaleOrDuplicate);
        tcs2.Task.IsCompleted.Should().BeFalse("attempt 1's completion must not resolve attempt 2");

        // Attempt 2's own completion still routes normally.
        registry.RouteCompletion(DeploymentId, TargetId, attempt2, Ok())
            .Should().Be(SubPlanCompletionRoute.ResolvedPending);
        (await tcs2.Task).Success.Should().BeTrue();
    }

    [Fact]
    public void Completion_for_a_cancelled_attempt_with_no_new_slot_is_swallowed()
    {
        // The wave was cancelled (e.g. worker bailed) and nothing re-registered.
        // The late completion must be swallowed — NOT fall through to the DB
        // fallback where it could finalize a non-terminal parent task.
        var registry = new PendingSubPlanRegistry();
        var dispatchId = Guid.NewGuid();
        registry.Register(DeploymentId, TargetId, dispatchId, new TaskCompletionSource<SubPlanResult>());
        registry.Cancel(DeploymentId, TargetId, "worker bailed");

        registry.RouteCompletion(DeploymentId, TargetId, dispatchId, Ok())
            .Should().Be(SubPlanCompletionRoute.StaleOrDuplicate);
    }

    [Fact]
    public void Unknown_dispatch_routes_to_the_fallback()
    {
        // Runbook runs (never registered) and post-server-restart lates (registry
        // state died with the process) take the direct DB finalize path, which
        // is IsTerminal-guarded downstream.
        var registry = new PendingSubPlanRegistry();

        registry.RouteCompletion(DeploymentId, TargetId, Guid.NewGuid(), Ok())
            .Should().Be(SubPlanCompletionRoute.NoPendingSubPlan);
    }

    [Fact]
    public async Task Legacy_empty_dispatch_matches_any_open_slot()
    {
        // Guid.Empty = "no key" (offline-era plan / pre-B2 agent): pre-B2
        // match-by-(deployment, target) behaviour is preserved.
        var registry = new PendingSubPlanRegistry();
        var tcs = new TaskCompletionSource<SubPlanResult>();
        registry.Register(DeploymentId, TargetId, Guid.NewGuid(), tcs);

        registry.RouteCompletion(DeploymentId, TargetId, Guid.Empty, Ok())
            .Should().Be(SubPlanCompletionRoute.ResolvedPending);
        (await tcs.Task).Success.Should().BeTrue();
    }

    [Fact]
    public void Legacy_empty_dispatch_with_no_slot_always_falls_back()
    {
        // Guid.Empty is never retired — otherwise the first legacy completion
        // would poison the shared "no key" marker and misroute every later one.
        var registry = new PendingSubPlanRegistry();
        registry.Register(DeploymentId, TargetId, Guid.Empty, new TaskCompletionSource<SubPlanResult>());
        registry.RouteCompletion(DeploymentId, TargetId, Guid.Empty, Ok())
            .Should().Be(SubPlanCompletionRoute.ResolvedPending);

        registry.RouteCompletion(DeploymentId, TargetId, Guid.Empty, Ok())
            .Should().Be(SubPlanCompletionRoute.NoPendingSubPlan);
        registry.RouteCompletion(Guid.NewGuid(), TargetId, Guid.Empty, Ok())
            .Should().Be(SubPlanCompletionRoute.NoPendingSubPlan);
    }

    [Fact]
    public void Stale_step_report_does_not_pollute_the_new_attempts_bag()
    {
        var registry = new PendingSubPlanRegistry();
        var attempt1 = Guid.NewGuid();
        var attempt2 = Guid.NewGuid();

        registry.Register(DeploymentId, TargetId, attempt1, new TaskCompletionSource<SubPlanResult>());
        registry.Cancel(DeploymentId, TargetId, "wave timed out");
        registry.Register(DeploymentId, TargetId, attempt2, new TaskCompletionSource<SubPlanResult>());

        // Stale (attempt 1) report — dropped; current (attempt 2) — recorded;
        // legacy Empty — recorded against whatever slot is open.
        registry.RecordStepResult(DeploymentId, TargetId, attempt1, MakeResult("stale"));
        registry.RecordStepResult(DeploymentId, TargetId, attempt2, MakeResult("current"));
        registry.RecordStepResult(DeploymentId, TargetId, Guid.Empty, MakeResult("legacy"));

        registry.DrainStepResults(DeploymentId, TargetId)
            .Select(r => r.StepName).Should().Equal(["current", "legacy"]);
    }

    private static SubPlanStepResult MakeResult(string name) =>
        new(StepIndex: 0, StepName: name, Success: true, ErrorMessage: null,
            Outputs: new Dictionary<string, string>());
}
