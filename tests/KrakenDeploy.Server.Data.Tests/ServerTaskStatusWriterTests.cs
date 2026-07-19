using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Interceptors;
using KrakenDeploy.Server.Data.Spaces;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// B5 (T1-1) — the guarded status writer + the xmin concurrency token it rides
/// on. Every pre-B5 status writer was a read-check-write: the terminal guard
/// was correct but a concurrent verdict landing between the check and the save
/// was silently overwritten (lost update). These tests pin the new semantics
/// against real Postgres: the token is live, the writer yields to a terminal
/// verdict no matter when it lands, retries through unrelated xmin churn
/// (lease renewals — log-sequence bumps no longer touch this row since E-D moved
/// the counter to task_log_counters), and exactly one of two racing terminal
/// writers wins.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class ServerTaskStatusWriterTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Transition_yields_when_the_row_went_terminal_after_load()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var taskId = await SeedTaskAsync(harness);

        await using var db = harness.CreateContext();
        var task = await db.ServerTasks.IgnoreQueryFilters().FirstAsync(t => t.Id == taskId);

        // The exact pre-B5 lost-update window: the writer loaded the row,
        // then the operator's cancel lands before the writer saves.
        var cancelStamp = DateTimeOffset.UtcNow;
        await using (var other = harness.CreateContext())
        {
            await other.ServerTasks.IgnoreQueryFilters()
                .Where(t => t.Id == taskId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status, DeploymentStatus.Cancelled)
                    .SetProperty(t => t.CompletedUtc, cancelStamp));
        }

        var wrote = await ServerTaskStatusWriter.TryTransitionAsync(
            db, task, t =>
            {
                t.Status = DeploymentStatus.Succeeded;
                t.CompletedUtc = DateTimeOffset.UtcNow;
            });

        wrote.Should().BeFalse("the concurrent cancel is the recorded verdict");
        task.Status.Should().Be(DeploymentStatus.Cancelled,
            "on refusal the tracked entity holds the authoritative status");

        await using var verify = harness.CreateContext();
        var persisted = await verify.ServerTasks.IgnoreQueryFilters().FirstAsync(t => t.Id == taskId);
        persisted.Status.Should().Be(DeploymentStatus.Cancelled);
        persisted.CompletedUtc.Should().BeCloseTo(cancelStamp, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Stale_tracked_entity_save_throws_without_the_writer()
    {
        // Proves the xmin token is actually live — and why the reload-first
        // writer is mandatory: ANY update of the row (here the same raw
        // lease-renewal bump ServerTaskLease issues out-of-band) stales a
        // tracked entity's token, even though no status changed at all.
        await using var harness = new OrchestratorTestHarness(postgres);
        var taskId = await SeedTaskAsync(harness);

        await using var db = harness.CreateContext();
        var task = await db.ServerTasks.IgnoreQueryFilters().FirstAsync(t => t.Id == taskId);

        await using (var other = harness.CreateContext())
        {
            await other.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE server_tasks SET lease_until = {DateTimeOffset.UtcNow} WHERE id = {taskId}");
        }

        task.Status = DeploymentStatus.Succeeded;
        var act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    [Fact]
    public async Task Unrelated_churn_between_reload_and_save_is_retried()
    {
        // Churn landing INSIDE the writer's reload→save window (the case the
        // bounded retry exists for): the first apply invocation bumps the row
        // from another connection, so the first save conflicts; the retry
        // reloads, re-applies and wins.
        await using var harness = new OrchestratorTestHarness(postgres);
        var taskId = await SeedTaskAsync(harness);

        await using var db = harness.CreateContext();
        var task = await db.ServerTasks.IgnoreQueryFilters().FirstAsync(t => t.Id == taskId);

        var applyCalls = 0;
        var wrote = await ServerTaskStatusWriter.TryTransitionAsync(
            db, task, t =>
            {
                if (++applyCalls == 1)
                {
                    using var other = harness.CreateContext();
                    other.Database.ExecuteSqlInterpolated(
                        $"UPDATE server_tasks SET lease_until = {DateTimeOffset.UtcNow} WHERE id = {taskId}");
                }
                t.Status = DeploymentStatus.Succeeded;
                t.CompletedUtc = DateTimeOffset.UtcNow;
            });

        wrote.Should().BeTrue("non-terminal churn must be absorbed, not surfaced");
        applyCalls.Should().Be(2, "the first save conflicts on the bumped xmin and is retried");

        await using var verify = harness.CreateContext();
        (await verify.ServerTasks.IgnoreQueryFilters().FirstAsync(t => t.Id == taskId))
            .Status.Should().Be(DeploymentStatus.Succeeded);
    }

    [Fact]
    public async Task Deleted_row_reports_false_instead_of_throwing()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var taskId = await SeedTaskAsync(harness);

        await using var db = harness.CreateContext();
        var task = await db.ServerTasks.IgnoreQueryFilters().FirstAsync(t => t.Id == taskId);

        // Retention pruning deletes terminal tasks; a late writer must treat
        // "row gone" as "nothing to transition", not crash the caller.
        await using (var other = harness.CreateContext())
        {
            await other.ServerTasks.IgnoreQueryFilters()
                .Where(t => t.Id == taskId)
                .ExecuteDeleteAsync();
        }

        var wrote = await ServerTaskStatusWriter.TryTransitionAsync(
            db, task, t => t.Status = DeploymentStatus.Failed);

        wrote.Should().BeFalse();
    }

    [Fact]
    public async Task Custom_transition_guard_is_evaluated_against_the_fresh_status()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var taskId = await SeedTaskAsync(harness);

        await using var db = harness.CreateContext();
        var task = await db.ServerTasks.IgnoreQueryFilters().FirstAsync(t => t.Id == taskId);

        // The offline-ingest guard: only a PendingOfflineResult row may accept
        // a result. The seeded row is Queued — refused, nothing written.
        var wrote = await ServerTaskStatusWriter.TryTransitionAsync(
            db, task, t => t.Status = DeploymentStatus.Succeeded,
            canTransitionFrom: static s => s == DeploymentStatus.PendingOfflineResult);

        wrote.Should().BeFalse();
        await using var verify = harness.CreateContext();
        (await verify.ServerTasks.IgnoreQueryFilters().FirstAsync(t => t.Id == taskId))
            .Status.Should().Be(DeploymentStatus.Queued, "a refused transition writes nothing");
    }

    [Fact]
    public async Task Exactly_one_of_two_racing_terminal_writers_wins()
    {
        // Cancel vs finalize, both passing the guard on their initial read:
        // the xmin token serializes them — the loser's save conflicts, its
        // retry reloads, sees the winner's terminal verdict and yields.
        await using var harness = new OrchestratorTestHarness(postgres);
        var taskId = await SeedTaskAsync(harness);

        await using var dbA = harness.CreateContext();
        await using var dbB = harness.CreateContext();
        var taskA = await dbA.ServerTasks.IgnoreQueryFilters().FirstAsync(t => t.Id == taskId);
        var taskB = await dbB.ServerTasks.IgnoreQueryFilters().FirstAsync(t => t.Id == taskId);

        var writeA = ServerTaskStatusWriter.TryTransitionAsync(
            dbA, taskA, t => { t.Status = DeploymentStatus.Succeeded; t.CompletedUtc = DateTimeOffset.UtcNow; });
        var writeB = ServerTaskStatusWriter.TryTransitionAsync(
            dbB, taskB, t => { t.Status = DeploymentStatus.Cancelled; t.CompletedUtc = DateTimeOffset.UtcNow; });
        var results = await Task.WhenAll(writeA, writeB);

        results.Count(r => r).Should().Be(1, "terminal state is written exactly once");

        await using var verify = harness.CreateContext();
        var persisted = await verify.ServerTasks.IgnoreQueryFilters().FirstAsync(t => t.Id == taskId);
        var expected = results[0] ? DeploymentStatus.Succeeded : DeploymentStatus.Cancelled;
        persisted.Status.Should().Be(expected, "the persisted verdict belongs to the winning writer");
    }

    [Fact]
    public async Task Retried_transition_emits_exactly_one_audit_entry()
    {
        // The AuditLogInterceptor stages an AuditEntry per dirty auditable
        // entity on EVERY SavingChanges. A failed save used to leave that row
        // tracked, so the writer's concurrency retry would persist the failed
        // attempt's audit record alongside the winning attempt's — two
        // "Deployment.Updated" rows for one transition, the first describing
        // a save that never happened. The interceptor now detaches its staged
        // cohort when the save fails.
        await using var harness = new OrchestratorTestHarness(postgres);
        var taskId = await SeedTaskAsync(harness);

        // Bespoke context WITH the audit-log interceptor (the fixture's
        // default context omits it), mirroring the production registration.
        var spaceContext = new DefaultSpaceContext();
        var options = new DbContextOptionsBuilder<KrakenDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(
                new AuditableEntityInterceptor(TimeProvider.System),
                new AuditLogInterceptor(new HttpContextAccessor(), TimeProvider.System),
                new SpaceScopingInterceptor(spaceContext))
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var db = new KrakenDbContext(options, spaceContext);

        var task = await db.ServerTasks.IgnoreQueryFilters().FirstAsync(t => t.Id == taskId);

        var applyCalls = 0;
        var wrote = await ServerTaskStatusWriter.TryTransitionAsync(
            db, task, t =>
            {
                if (++applyCalls == 1)
                {
                    using var other = harness.CreateContext();
                    other.Database.ExecuteSqlInterpolated(
                        $"UPDATE server_tasks SET lease_until = {DateTimeOffset.UtcNow} WHERE id = {taskId}");
                }
                t.Status = DeploymentStatus.Cancelled;
                t.CompletedUtc = DateTimeOffset.UtcNow;
            });

        wrote.Should().BeTrue();
        applyCalls.Should().Be(2, "the first save must conflict for this test to exercise the retry");

        await using var verify = harness.CreateContext();
        var audits = await verify.Set<KrakenDeploy.Server.Core.Domain.Audit.AuditEntry>()
            .Where(a => a.SubjectId == taskId.ToString() && a.EventType == "Deployment.Updated")
            .ToListAsync();
        audits.Should().HaveCount(1,
            "the failed attempt's staged audit row must be detached, not persisted by the retry");
        audits[0].AfterJson.Should().NotContain("xmin",
            "the concurrency token is bookkeeping, not audit-diff material");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<Guid> SeedTaskAsync(OrchestratorTestHarness harness)
    {
        // Unique names per call — the Postgres fixture is shared per class.
        var tag = Guid.NewGuid().ToString("N")[..8];
        var project = await harness.SeedProjectAsync($"stw-p-{tag}");
        var env = await harness.SeedEnvironmentAsync($"stw-e-{tag}");
        var targets = await harness.SeedTargetsAsync($"stw-t-{tag}");
        var release = await harness.SeedReleaseAsync(project.Id, "1.0", StepBuilder.Script("s1"));
        return await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
    }
}
