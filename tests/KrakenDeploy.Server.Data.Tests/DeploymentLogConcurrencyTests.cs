using FluentAssertions;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;
using KrakenDeploy.Server.Transport;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Focused concurrency test for the orchestrator's per-write deployment-log
/// path. <see cref="DeploymentWorker"/> holds ONE <c>KrakenDbContext</c> per
/// dispatch, but wave steps and rolling-deployment targets emit log lines in
/// parallel; those writes go through <c>AppendConcurrentLogAsync</c>, which
/// gives each write its OWN short-lived context because EF Core's DbContext is
/// not safe for concurrent operations.
///
/// <para>
/// The orchestrator E2E harness cannot exercise this: its fake agent resolves
/// sub-plan dispatches synchronously, so the real parallel fan-out collapses to
/// sequential execution. This test instead drives the helper from genuinely
/// parallel tasks released at the same instant, against a real Postgres, and
/// asserts the three invariants the fix guarantees:
/// <list type="number">
///   <item>no <c>InvalidOperationException</c> ("a second operation was started
///         on this context") — the old shared-context design could throw here;</item>
///   <item>every concurrent write persists its own row (none lost);</item>
///   <item>sequence numbers are unique (<see cref="LogSequencer"/> holds under
///         contention).</item>
/// </list>
/// </para>
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class DeploymentLogConcurrencyTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Concurrent_log_writes_all_persist_with_unique_sequences()
    {
        await using var harness = new OrchestratorTestHarness(postgres);

        // Minimal FK chain — TaskLogLiveEntry.TaskId is an enforced FK,
        // so the log rows need a real Deployment to hang off.
        var project = await harness.SeedProjectAsync();
        var env = await harness.SeedEnvironmentAsync();
        var targets = await harness.SeedTargetsAsync("t1");
        var release = await harness.SeedReleaseAsync(project.Id, "1.0.0");
        var deploymentId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);

        // One LogSequencer per dispatch, shared across the parallel writers —
        // exactly how the orchestrator wires it (new ctor: scope factory +
        // TimeProvider + task id; the sequencer allocates via TaskLogService).
        var logSeq = new LogSequencer(postgres.ScopeFactory, TimeProvider.System, deploymentId);

        const int writers = 64;

        // Park every task on a gate, then release them together so they hit
        // CreateDbContextAsync + SaveChangesAsync simultaneously. Task.Run puts
        // each on a pool thread; RunContinuationsAsynchronously stops the
        // release from running the continuations inline on one thread.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = Enumerable.Range(0, writers)
            .Select(_ => Task.Run(async () =>
            {
                await gate.Task.ConfigureAwait(false);
                await OrchestratorTestHarness.AppendConcurrentLogForTestAsync(
                    deploymentId, logSeq, "info", "concurrent log line").ConfigureAwait(false);
            }))
            .ToArray();

        gate.SetResult();

        // If the writers shared one DbContext, this would throw
        // "A second operation was started on this context...".
        await Task.WhenAll(writes);

        await using var db = harness.CreateContext();
        var entries = await db.TaskLogLive
            .Where(e => e.TaskId == deploymentId)
            .ToListAsync();

        entries.Should().HaveCount(writers,
            "every concurrent write must persist its own row");
        entries.Select(e => e.Sequence).Should().OnlyHaveUniqueItems(
            "LogSequencer must hand each concurrent writer a distinct sequence");
    }
}
