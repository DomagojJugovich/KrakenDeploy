using System.Threading.Channels;
using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data.Accounts;
using KrakenDeploy.Server.Data.Jobs;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// B1/T0-1 — the dispatch reconciler (boot + minutely sweep). A server crash
/// used to strand Queued rows forever (their wake-up lived only in the
/// in-process channel) and leave mid-run deployments stuck at Running with no
/// owner. The reconciler re-signals stale Queued tasks (both kinds, to the
/// right channel) and fails Running DEPLOYMENTS whose lease expired — while a
/// LIVE lease (a draining blue-green slot, a long step) is never touched, and
/// runbook runs / PendingOfflineResult rows are never reconciled at all.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class DispatchReconcileTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Stale_queued_deployment_is_resignalled()
    {
        var id = await SeedDeploymentAsync();
        await AgeAsync(id, createdAgo: TimeSpan.FromMinutes(10));
        var (job, deployments, _, _) = NewJob();

        await job.ExecuteAsync(CancellationToken.None);

        deployments.Reader.TryRead(out var item).Should().BeTrue(
            "a Queued task older than the grace lost its wake-up in a restart and must be re-signalled");
        item.Id.Should().Be(id);
    }

    [Fact]
    public async Task Fresh_queued_deployment_is_left_alone()
    {
        var id = await SeedDeploymentAsync(); // CreatedUtc = now, inside the grace window
        var (job, deployments, _, _) = NewJob();

        await job.ExecuteAsync(CancellationToken.None);

        // Other tests in this class DB may legitimately be re-signalled; the
        // assertion is that THIS fresh row is not among them.
        var signalled = new List<Guid>();
        while (deployments.Reader.TryRead(out var item))
        {
            signalled.Add(item.Id);
        }
        signalled.Should().NotContain(id,
            "a just-created Queued row's original wake-up is still in flight");
    }

    [Fact]
    public async Task Expired_lease_running_deployment_is_failed_with_interrupted_audit()
    {
        var id = await SeedDeploymentAsync();
        await SetRunningAsync(id, leaseUntil: DateTimeOffset.UtcNow.AddMinutes(-1), claimedBy: "kraken:dead-node");
        var (job, _, _, audit) = NewJob();

        await job.ExecuteAsync(CancellationToken.None);

        await using var db = postgres.CreateContext();
        var task = await db.ServerTasks.IgnoreQueryFilters().AsNoTracking().FirstAsync(t => t.Id == id);
        task.Status.Should().Be(DeploymentStatus.Failed,
            "an expired lease means the orchestrating process died; the run is unresumable");
        task.CompletedUtc.Should().NotBeNull();
        task.ClaimedBy.Should().BeNull();
        task.LeaseUntil.Should().BeNull();

        audit.Entries.Should().ContainSingle(e =>
            e.EventType == AuditEventType.DeploymentInterrupted && e.SubjectId == id.ToString());
    }

    [Fact]
    public async Task Null_lease_running_deployment_is_failed_too()
    {
        // Rows from before the lease feature (or a claim that predates an upgrade).
        var id = await SeedDeploymentAsync();
        await SetRunningAsync(id, leaseUntil: null, claimedBy: null);
        var (job, _, _, audit) = NewJob();

        await job.ExecuteAsync(CancellationToken.None);

        await using var db = postgres.CreateContext();
        (await db.ServerTasks.IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.Id == id).Select(t => t.Status).FirstAsync())
            .Should().Be(DeploymentStatus.Failed);
        audit.Entries.Should().ContainSingle(e =>
            e.EventType == AuditEventType.DeploymentInterrupted && e.SubjectId == id.ToString());
    }

    [Fact]
    public async Task Live_lease_running_deployment_is_never_touched()
    {
        // The blue-green guarantee: a draining slot keeps renewing its lease;
        // the freshly booted slot's reconciler must leave its runs alone.
        var id = await SeedDeploymentAsync();
        await SetRunningAsync(id, leaseUntil: DateTimeOffset.UtcNow.AddMinutes(4), claimedBy: "kraken:draining-slot");
        var (job, _, _, audit) = NewJob();

        await job.ExecuteAsync(CancellationToken.None);

        await using var db = postgres.CreateContext();
        (await db.ServerTasks.IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.Id == id).Select(t => t.Status).FirstAsync())
            .Should().Be(DeploymentStatus.Running, "a live lease means a live owner — hands off");
        audit.Entries.Should().NotContain(e => e.SubjectId == id.ToString());
    }

    [Fact]
    public async Task PendingOfflineResult_is_never_reconciled()
    {
        var id = await SeedDeploymentAsync();
        await using (var db = postgres.CreateContext())
        {
            await db.ServerTasks.IgnoreQueryFilters().Where(t => t.Id == id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status, DeploymentStatus.PendingOfflineResult)
                    .SetProperty(t => t.CreatedUtc, DateTimeOffset.UtcNow.AddDays(-3)));
        }
        var (job, deployments, _, audit) = NewJob();

        await job.ExecuteAsync(CancellationToken.None);

        var signalled = new List<Guid>();
        while (deployments.Reader.TryRead(out var item))
        {
            signalled.Add(item.Id);
        }
        signalled.Should().NotContain(id,
            "a task parked awaiting an out-of-band offline result is neither stranded nor orphaned");
        audit.Entries.Should().NotContain(e => e.SubjectId == id.ToString());
        await using var check = postgres.CreateContext();
        (await check.ServerTasks.IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.Id == id).Select(t => t.Status).FirstAsync())
            .Should().Be(DeploymentStatus.PendingOfflineResult);
    }

    // ── B3: overdue runbook runs ───────────────────────────────────────────

    [Fact]
    public async Task Runbook_run_with_expired_lease_is_failed_with_interrupted_audit()
    {
        // The dispatch process died between the atomic claim and the agent
        // hand-off — the plan never reached the agent, and step 3 (deployments
        // only) would never touch it.
        var id = await SeedRunbookRunAsync();
        await SetRunbookRunningAsync(id,
            leaseUntil: DateTimeOffset.UtcNow.AddMinutes(-1),
            claimedBy: "kraken:dead-node",
            startedAgo: TimeSpan.FromMinutes(5));
        var (job, _, _, audit) = NewJob();

        await job.ExecuteAsync(CancellationToken.None);

        await using var db = postgres.CreateContext();
        var run = await db.ServerTasks.IgnoreQueryFilters().AsNoTracking().FirstAsync(t => t.Id == id);
        run.Status.Should().Be(DeploymentStatus.Failed);
        run.CompletedUtc.Should().NotBeNull();
        run.LeaseUntil.Should().BeNull();

        audit.Entries.Should().ContainSingle(e =>
            e.EventType == AuditEventType.RunbookRunInterrupted && e.SubjectId == id.ToString());
    }

    [Fact]
    public async Task Agent_owned_runbook_run_past_the_max_duration_is_failed_with_timeout_audit()
    {
        // Lease released at hand-off (agent-owned), agent never called back —
        // pre-B3 this row stayed Running forever.
        var id = await SeedRunbookRunAsync();
        await SetRunbookRunningAsync(id,
            leaseUntil: null, claimedBy: null, startedAgo: TimeSpan.FromMinutes(10));
        var (job, _, _, audit) = NewJob(new EngineOptions
        {
            MaxRunbookRunDuration = TimeSpan.FromMinutes(5),
        });

        await job.ExecuteAsync(CancellationToken.None);

        await using var db = postgres.CreateContext();
        (await db.ServerTasks.IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.Id == id).Select(t => t.Status).FirstAsync())
            .Should().Be(DeploymentStatus.Failed);
        audit.Entries.Should().ContainSingle(e =>
            e.EventType == AuditEventType.RunbookRunTimedOut && e.SubjectId == id.ToString());
    }

    [Fact]
    public async Task Agent_owned_runbook_run_within_the_max_duration_is_left_alone()
    {
        var id = await SeedRunbookRunAsync();
        await SetRunbookRunningAsync(id,
            leaseUntil: null, claimedBy: null, startedAgo: TimeSpan.FromMinutes(10));
        var (job, _, _, audit) = NewJob(new EngineOptions
        {
            MaxRunbookRunDuration = TimeSpan.FromHours(1),
        });

        await job.ExecuteAsync(CancellationToken.None);

        await using var db = postgres.CreateContext();
        (await db.ServerTasks.IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.Id == id).Select(t => t.Status).FirstAsync())
            .Should().Be(DeploymentStatus.Running,
                "an agent-owned run inside the ceiling is presumed in flight — the B2 " +
                "outbox delivers its completion even across disconnects");
        audit.Entries.Should().NotContain(e => e.SubjectId == id.ToString());
    }

    [Fact]
    public async Task Live_lease_runbook_run_is_never_touched()
    {
        // Mid-dispatch (claim taken, hand-off not yet done) with a healthy
        // renewing lease — hands off, exactly like deployments.
        var id = await SeedRunbookRunAsync();
        await SetRunbookRunningAsync(id,
            leaseUntil: DateTimeOffset.UtcNow.AddMinutes(4),
            claimedBy: "kraken:live-node",
            startedAgo: TimeSpan.FromHours(3)); // old StartedUtc must NOT matter while leased
        var (job, _, _, audit) = NewJob(new EngineOptions
        {
            MaxRunbookRunDuration = TimeSpan.FromMinutes(5),
        });

        await job.ExecuteAsync(CancellationToken.None);

        await using var db = postgres.CreateContext();
        (await db.ServerTasks.IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.Id == id).Select(t => t.Status).FirstAsync())
            .Should().Be(DeploymentStatus.Running);
        audit.Entries.Should().NotContain(e => e.SubjectId == id.ToString());
    }

    // ── E9 (INTERIM): disconnect-aware runbook reap ────────────────────────

    [Fact]
    public async Task Agent_owned_runbook_run_with_a_long_disconnected_target_is_failed()
    {
        // Agent-owned (lease released), well inside the 1 h ceiling, but its target
        // has been silent past the disconnect grace and the registry sees no live
        // tunnel — the agent died mid-run. Proves the disconnect arm fires, NOT the
        // ceiling (started only 10 min ago).
        var (runId, _) = await SeedRunbookRunWithTargetAsync(
            targetLastSeen: DateTimeOffset.UtcNow.AddMinutes(-10));
        await SetRunbookRunningAsync(runId,
            leaseUntil: null, claimedBy: null, startedAgo: TimeSpan.FromMinutes(10));
        var (job, _, _, audit) = NewJob(new EngineOptions
        {
            AgentDisconnectWaveGrace = TimeSpan.FromMinutes(2),
            MaxRunbookRunDuration    = TimeSpan.FromHours(1),
        }); // default probe: target not connected

        await job.ExecuteAsync(CancellationToken.None);

        await using var db = postgres.CreateContext();
        (await db.ServerTasks.IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.Id == runId).Select(t => t.Status).FirstAsync())
            .Should().Be(DeploymentStatus.Failed,
                "the agent has been continuously disconnected past the grace");
        audit.Entries.Should().ContainSingle(e =>
            e.EventType == AuditEventType.RunbookRunInterrupted && e.SubjectId == runId.ToString());
    }

    [Fact]
    public async Task Agent_owned_runbook_run_whose_target_is_still_connected_is_left_alone()
    {
        // Stale heartbeat, but the node-local registry still sees a live tunnel
        // (a fresh reconnect whose heartbeat hasn't flushed) — fail-closed: leave it.
        var (runId, targetId) = await SeedRunbookRunWithTargetAsync(
            targetLastSeen: DateTimeOffset.UtcNow.AddMinutes(-10));
        await SetRunbookRunningAsync(runId,
            leaseUntil: null, claimedBy: null, startedAgo: TimeSpan.FromMinutes(10));
        var probe = new StubAgentLivenessProbe();
        probe.Connected.Add(targetId);
        var (job, _, _, audit) = NewJob(new EngineOptions
        {
            AgentDisconnectWaveGrace = TimeSpan.FromMinutes(2),
            MaxRunbookRunDuration    = TimeSpan.FromHours(1),
        }, probe);

        await job.ExecuteAsync(CancellationToken.None);

        await using var db = postgres.CreateContext();
        (await db.ServerTasks.IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.Id == runId).Select(t => t.Status).FirstAsync())
            .Should().Be(DeploymentStatus.Running, "the registry still sees the agent connected");
        audit.Entries.Should().NotContain(e => e.SubjectId == runId.ToString());
    }

    [Fact]
    public async Task Agent_owned_runbook_run_with_a_recent_heartbeat_is_left_alone()
    {
        // Not connected on THIS node, but the shared-DB heartbeat is fresh (the
        // multi-node case: the agent is connected to another node). The LastSeenUtc
        // guard keeps the reap from firing.
        var (runId, _) = await SeedRunbookRunWithTargetAsync(
            targetLastSeen: DateTimeOffset.UtcNow.AddSeconds(-10));
        await SetRunbookRunningAsync(runId,
            leaseUntil: null, claimedBy: null, startedAgo: TimeSpan.FromMinutes(10));
        var (job, _, _, audit) = NewJob(new EngineOptions
        {
            AgentDisconnectWaveGrace = TimeSpan.FromMinutes(2),
            MaxRunbookRunDuration    = TimeSpan.FromHours(1),
        }); // default probe: not connected on this node

        await job.ExecuteAsync(CancellationToken.None);

        await using var db = postgres.CreateContext();
        (await db.ServerTasks.IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.Id == runId).Select(t => t.Status).FirstAsync())
            .Should().Be(DeploymentStatus.Running, "a fresh heartbeat means the agent is alive somewhere");
        audit.Entries.Should().NotContain(e => e.SubjectId == runId.ToString());
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private (ScheduledDeploymentDispatchJob Job,
             Channel<TenantWorkItem> Deployments,
             RunbookRunChannel Runbooks,
             TestAuditLog Audit) NewJob(
        EngineOptions? engineOptions = null, IAgentLivenessProbe? livenessProbe = null)
    {
        var deployments = Channel.CreateUnbounded<TenantWorkItem>();
        var runbooks = new RunbookRunChannel();
        var audit = new TestAuditLog();
        var job = new ScheduledDeploymentDispatchJob(
            postgres, deployments, runbooks, TimeProvider.System,
            new DisabledAccountContext(), audit,
            Microsoft.Extensions.Options.Options.Create(engineOptions ?? new EngineOptions()),
            livenessProbe ?? new StubAgentLivenessProbe(),
            NullLogger<ScheduledDeploymentDispatchJob>.Instance);
        return (job, deployments, runbooks, audit);
    }

    private async Task<Guid> SeedDeploymentAsync()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync($"rp-{Guid.NewGuid():N}"[..16]);
        var env = await harness.SeedEnvironmentAsync($"re-{Guid.NewGuid():N}"[..16]);
        var targets = await harness.SeedTargetsAsync("rec-t1");
        var release = await harness.SeedReleaseAsync(project.Id, "1.0", StepBuilder.Script("s1"));
        return await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
    }

    private async Task AgeAsync(Guid id, TimeSpan createdAgo)
    {
        await using var db = postgres.CreateContext();
        await db.ServerTasks.IgnoreQueryFilters().Where(t => t.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(
                t => t.CreatedUtc, DateTimeOffset.UtcNow - createdAgo));
    }

    /// <summary>Seeds a minimum-viable RunbookRun (project + env + runbook +
    /// Queued run) in the Default Space.</summary>
    private async Task<Guid> SeedRunbookRunAsync()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync($"rbp-{Guid.NewGuid():N}"[..16]);
        var env = await harness.SeedEnvironmentAsync($"rbe-{Guid.NewGuid():N}"[..16]);

        await using var db = postgres.CreateContext();
        var runbook = new KrakenDeploy.Server.Core.Domain.Runbooks.Runbook
        {
            SpaceId   = KrakenDeploy.Server.Core.Domain.Common.WellKnown.DefaultSpaceId,
            ProjectId = project.Id,
            Name      = $"rb-{Guid.NewGuid():N}"[..12],
        };
        db.Add(runbook);
        await db.SaveChangesAsync();

        var run = new KrakenDeploy.Server.Core.Domain.Runbooks.RunbookRun
        {
            SpaceId       = KrakenDeploy.Server.Core.Domain.Common.WellKnown.DefaultSpaceId,
            ProjectId     = project.Id,
            EnvironmentId = env.Id,
            RunbookId     = runbook.Id,
            Status        = DeploymentStatus.Queued,
        };
        db.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    private async Task SetRunbookRunningAsync(
        Guid id, DateTimeOffset? leaseUntil, string? claimedBy, TimeSpan startedAgo)
    {
        await using var db = postgres.CreateContext();
        await db.ServerTasks.IgnoreQueryFilters().Where(t => t.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, DeploymentStatus.Running)
                .SetProperty(t => t.StartedUtc, DateTimeOffset.UtcNow - startedAgo)
                .SetProperty(t => t.LeaseUntil, leaseUntil)
                .SetProperty(t => t.ClaimedBy, claimedBy)
                .SetProperty(t => t.CreatedUtc, DateTimeOffset.UtcNow - startedAgo));
    }

    /// <summary>Seeds an agent-owned-shaped RunbookRun with ONE assigned target
    /// carrying the given last-heartbeat time. Returns (runId, targetId).</summary>
    private async Task<(Guid RunId, Guid TargetId)> SeedRunbookRunWithTargetAsync(
        DateTimeOffset targetLastSeen)
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync($"rbp-{Guid.NewGuid():N}"[..16]);
        var env = await harness.SeedEnvironmentAsync($"rbe-{Guid.NewGuid():N}"[..16]);

        await using var db = postgres.CreateContext();
        var target = new DeploymentTarget
        {
            SpaceId       = WellKnown.DefaultSpaceId,
            Name          = $"t-{Guid.NewGuid():N}"[..12],
            Roles         = ["web"],
            TransportMode = TransportMode.Reverse,
            Status        = TargetStatus.Online,
            LastSeenUtc   = targetLastSeen,
        };
        db.DeploymentTargets.Add(target);

        var runbook = new KrakenDeploy.Server.Core.Domain.Runbooks.Runbook
        {
            SpaceId   = WellKnown.DefaultSpaceId,
            ProjectId = project.Id,
            Name      = $"rb-{Guid.NewGuid():N}"[..12],
        };
        db.Add(runbook);
        await db.SaveChangesAsync();

        var run = new KrakenDeploy.Server.Core.Domain.Runbooks.RunbookRun
        {
            SpaceId       = WellKnown.DefaultSpaceId,
            ProjectId     = project.Id,
            EnvironmentId = env.Id,
            RunbookId     = runbook.Id,
            Status        = DeploymentStatus.Queued,
        };
        db.Add(run);
        await db.SaveChangesAsync();

        db.TaskTargetAssignments.Add(new TaskTargetAssignment
        {
            SpaceId  = WellKnown.DefaultSpaceId,
            TaskId   = run.Id,
            TargetId = target.Id,
            AddedUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (run.Id, target.Id);
    }

    private async Task SetRunningAsync(Guid id, DateTimeOffset? leaseUntil, string? claimedBy)
    {
        await using var db = postgres.CreateContext();
        await db.ServerTasks.IgnoreQueryFilters().Where(t => t.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, DeploymentStatus.Running)
                .SetProperty(t => t.StartedUtc, DateTimeOffset.UtcNow.AddMinutes(-30))
                .SetProperty(t => t.LeaseUntil, leaseUntil)
                .SetProperty(t => t.ClaimedBy, claimedBy)
                .SetProperty(t => t.CreatedUtc, DateTimeOffset.UtcNow.AddMinutes(-40)));
    }
}
