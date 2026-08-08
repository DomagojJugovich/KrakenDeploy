using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// The fail-closed predicate behind <c>GET /api/agents/task-in-flight</c>, over a real
/// database and every <see cref="DeploymentStatus"/>.
/// <para>
/// It had no test at all while it lived inline in the endpoint lambda: deleting the <c>!</c>
/// from its terminal check left the entire suite green, and the agent takes a false "idle" as
/// licence to replace its own install directory and exit mid-plan. Enumerating all statuses
/// rather than spot-checking two is the point — the failure mode this guards is a status whose
/// classification nobody thought about.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class AgentTaskInFlightQueryTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Theory]
    // Terminal — the swap may proceed.
    [InlineData(DeploymentStatus.Succeeded, false)]
    [InlineData(DeploymentStatus.SucceededWithWarnings, false)]
    [InlineData(DeploymentStatus.Failed, false)]
    [InlineData(DeploymentStatus.Cancelled, false)]
    // Non-terminal — the swap must NOT proceed.
    [InlineData(DeploymentStatus.Queued, true)]
    [InlineData(DeploymentStatus.Running, true)]
    [InlineData(DeploymentStatus.PendingOfflineResult, true)]
    // WP3's manual-intervention gate: the task is parked awaiting a human approve/reject, so
    // the agent may still be mid-plan and must not replace its own binary underneath it.
    [InlineData(DeploymentStatus.Paused, true)]
    public async Task Every_status_is_classified(DeploymentStatus status, bool expectInFlight)
    {
        var (targetId, taskId) = await SeedAsync();
        await SetStatusAsync(taskId, status);

        await using var db = postgres.CreateContext();
        var inFlight = await db.AnyNonTerminalForTargetAsync(targetId);

        inFlight.Should().Be(expectInFlight,
            $"{status} must classify the same way DeploymentStatusExtensions.IsTerminal does");
        // Cross-check against the authority itself, so adding a status without deciding its
        // classification cannot leave this test silently asserting the wrong thing.
        inFlight.Should().Be(!status.IsTerminal());
    }

    [Fact]
    public async Task All_statuses_are_covered_by_the_theory()
    {
        // The theory above is a hand-written list, and the risk this whole class exists for is
        // a NEW status nobody classified. If one is added, this fails until the list grows.
        // This guard has already earned its keep once: WP3 added Paused on main while F5 was in
        // flight, and merging the two turned this test red until Paused was classified
        // deliberately (non-terminal — a task parked at an approval gate may still be mid-plan
        // on the agent).
        Enum.GetValues<DeploymentStatus>().Should().HaveCount(8,
            "add the new DeploymentStatus to Every_status_is_classified and decide, " +
            "deliberately, whether an agent may replace its own binary while a task is in " +
            "that state");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_target_with_no_assignments_is_idle()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var target = (await harness.SeedTargetsAsync($"idle-{Guid.NewGuid():N}"[..20]))[0];

        await using var db = postgres.CreateContext();
        (await db.AnyNonTerminalForTargetAsync(target.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Another_targets_running_task_does_not_block_this_ones_swap()
    {
        // The predicate is per-TARGET. A fleet-wide read would freeze every agent's
        // self-upgrade for as long as anything anywhere is running.
        var (busyTarget, taskId) = await SeedAsync();
        await SetStatusAsync(taskId, DeploymentStatus.Running);

        await using var harness = new OrchestratorTestHarness(postgres);
        var idle = (await harness.SeedTargetsAsync($"unrelated-{Guid.NewGuid():N}"[..20]))[0];

        await using var db = postgres.CreateContext();
        (await db.AnyNonTerminalForTargetAsync(busyTarget)).Should().BeTrue();
        (await db.AnyNonTerminalForTargetAsync(idle.Id)).Should().BeFalse();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task<(Guid TargetId, Guid TaskId)> SeedAsync()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var project = await harness.SeedProjectAsync($"inflight-proj-{tag}");
        var env = await harness.SeedEnvironmentAsync($"inflight-env-{tag}");
        var targets = await harness.SeedTargetsAsync($"inflight-target-{tag}");
        var release = await harness.SeedReleaseAsync(project.Id, "1.0.0");
        var taskId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        return (targets[0].Id, taskId);
    }

    private async Task SetStatusAsync(Guid taskId, DeploymentStatus status)
    {
        await using var db = postgres.CreateContext();
        var task = await db.ServerTasks.IgnoreQueryFilters().FirstAsync(t => t.Id == taskId);
        task.Status = status;
        task.CompletedUtc = status.IsTerminal() ? DateTimeOffset.UtcNow : null;
        await db.SaveChangesAsync();
    }
}
