using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Lifecycles;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Runbooks;
using KrakenDeploy.Server.Core.Domain.Spaces;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Schema-hardening fix 6, decision 4: pruning an execution must delete its log
/// children through the fix-3 <c>ON DELETE CASCADE</c> FKs, for BOTH task kinds,
/// and retention must never touch a non-terminal (Running/Queued) run or its live
/// log tail.
/// <para>
/// Docker/Postgres-gated + real DI (<c>AddKrakenDeployData</c>) so the scoped
/// <see cref="ISpaceContext"/> flows into the query filter — and, decisively,
/// because the prune uses <c>ExecuteDeleteAsync</c> which relies on the DB-level
/// cascade. The EF InMemory provider does NOT enforce <c>ON DELETE CASCADE</c>, so
/// child deletion can only be proven against real Postgres.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class RetentionCascadeTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    // ── Deployment kind ─────────────────────────────────────────────────────

    [Fact]
    public async Task PruneAfterDeployment_cascade_deletes_the_pruned_deployment_log_children()
    {
        var spaceId = Guid.NewGuid();
        Guid keptId, prunedId;
        await using (var db = postgres.CreateContext())
        {
            var (envId, releaseId) = await SeedDeploymentGraphAsync(db, spaceId, keep: 1);
            var baseUtc = DateTimeOffset.UtcNow;
            var pruned = NewDeployment(spaceId, releaseId, envId, baseUtc.AddHours(-1));
            var kept   = NewDeployment(spaceId, releaseId, envId, baseUtc);
            db.Deployments.AddRange(pruned, kept);
            await db.SaveChangesAsync();
            AddLogChildren(db, pruned.Id);
            AddLogChildren(db, kept.Id);
            await db.SaveChangesAsync();
            prunedId = pruned.Id;
            keptId   = kept.Id;
        }

        await PruneDeploymentAsync(keptId);

        await using var check = postgres.CreateContext();
        (await check.Deployments.IgnoreQueryFilters().AnyAsync(d => d.Id == prunedId))
            .Should().BeFalse("keep=1 prunes the older successful deployment");
        (await check.TaskStepLogs.AnyAsync(l => l.TaskId == prunedId))
            .Should().BeFalse("the pruned deployment's task_step_logs must cascade away");
        (await check.TaskLogLive.AnyAsync(l => l.TaskId == prunedId))
            .Should().BeFalse("the pruned deployment's task_log_live rows must cascade away");

        (await check.Deployments.IgnoreQueryFilters().AnyAsync(d => d.Id == keptId))
            .Should().BeTrue("the newest successful deployment is retained");
        (await check.TaskStepLogs.AnyAsync(l => l.TaskId == keptId))
            .Should().BeTrue("the retained deployment keeps its step-log children");
        (await check.TaskLogLive.AnyAsync(l => l.TaskId == keptId))
            .Should().BeTrue("the retained deployment keeps its live-log children");
    }

    [Fact]
    public async Task PruneAfterDeployment_never_prunes_a_running_deployment_or_its_live_logs()
    {
        var spaceId = Guid.NewGuid();
        Guid runningId, succNewId, succOldId;
        await using (var db = postgres.CreateContext())
        {
            var (envId, releaseId) = await SeedDeploymentGraphAsync(db, spaceId, keep: 1);
            var baseUtc = DateTimeOffset.UtcNow;

            // A Running deployment OLDER than both successful ones — it must survive
            // even though retention is actively pruning around it.
            var running = NewDeployment(spaceId, releaseId, envId, completedUtc: null);
            running.Status = DeploymentStatus.Running;
            running.StartedUtc = baseUtc.AddHours(-3);
            var succOld = NewDeployment(spaceId, releaseId, envId, baseUtc.AddHours(-1));
            var succNew = NewDeployment(spaceId, releaseId, envId, baseUtc);
            db.Deployments.AddRange(running, succOld, succNew);
            await db.SaveChangesAsync();
            AddLogChildren(db, running.Id);   // live log tail on the running deployment
            await db.SaveChangesAsync();
            runningId = running.Id;
            succOldId = succOld.Id;
            succNewId = succNew.Id;
        }

        await PruneDeploymentAsync(succNewId);

        await using var check = postgres.CreateContext();
        (await check.Deployments.IgnoreQueryFilters().AnyAsync(d => d.Id == runningId))
            .Should().BeTrue("only == Succeeded is a retention candidate; a Running row is never eligible");
        (await check.TaskLogLive.AnyAsync(l => l.TaskId == runningId))
            .Should().BeTrue("the running deployment's live log tail must survive retention");
        (await check.Deployments.IgnoreQueryFilters().AnyAsync(d => d.Id == succOldId))
            .Should().BeFalse("the older SUCCEEDED deployment is still pruned around the running one");
        (await check.Deployments.IgnoreQueryFilters().AnyAsync(d => d.Id == succNewId))
            .Should().BeTrue("the newest successful deployment is retained");
    }

    // ── Runbook-run kind (the gap fix 6 closes) ─────────────────────────────

    [Fact]
    public async Task PruneAfterRunbookRun_cascade_deletes_the_pruned_run_log_children()
    {
        var spaceId = Guid.NewGuid();
        Guid keptId, prunedId;
        await using (var db = postgres.CreateContext())
        {
            var (envId, runbookId) = await SeedRunbookGraphAsync(db, spaceId);
            var baseUtc = DateTimeOffset.UtcNow;
            var pruned = NewRunbookRun(spaceId, runbookId, envId, baseUtc.AddHours(-1));
            var kept   = NewRunbookRun(spaceId, runbookId, envId, baseUtc);
            db.RunbookRuns.AddRange(pruned, kept);
            await db.SaveChangesAsync();
            AddLogChildren(db, pruned.Id);
            AddLogChildren(db, kept.Id);
            await db.SaveChangesAsync();
            prunedId = pruned.Id;
            keptId   = kept.Id;
        }

        // keepOverride: 1 exercises the prune without seeding DefaultRunbookRunKeep+1 runs.
        await PruneRunbookRunAsync(keptId, keepOverride: 1);

        await using var check = postgres.CreateContext();
        (await check.RunbookRuns.IgnoreQueryFilters().AnyAsync(r => r.Id == prunedId))
            .Should().BeFalse("keep=1 prunes the older successful run (runbook runs were never pruned before fix 6)");
        (await check.TaskStepLogs.AnyAsync(l => l.TaskId == prunedId))
            .Should().BeFalse("the pruned run's task_step_logs must cascade away");
        (await check.TaskLogLive.AnyAsync(l => l.TaskId == prunedId))
            .Should().BeFalse("the pruned run's task_log_live rows must cascade away");

        (await check.RunbookRuns.IgnoreQueryFilters().AnyAsync(r => r.Id == keptId))
            .Should().BeTrue("the newest successful run is retained");
        (await check.TaskStepLogs.AnyAsync(l => l.TaskId == keptId))
            .Should().BeTrue("the retained run keeps its step-log children");
    }

    // ── Service invocation (real DI so ISpaceContext reaches the query filter) ──

    private async Task PruneDeploymentAsync(Guid deploymentId)
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<RetentionService>()
            .PruneAfterDeploymentAsync(deploymentId);
    }

    private async Task PruneRunbookRunAsync(Guid runId, int keepOverride)
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<RetentionService>()
            .PruneAfterRunbookRunAsync(runId, keepOverride);
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddKrakenDeployData(postgres.ConnectionString);
        services.AddLogging();
        return services.BuildServiceProvider();
    }

    // ── Seeding ──────────────────────────────────────────────────────────────

    private static async Task<(Guid EnvId, Guid ReleaseId)> SeedDeploymentGraphAsync(
        KrakenDbContext db, Guid spaceId, int keep)
    {
        await SeedSpaceAndEnvAsync(db, spaceId);
        var env = await db.Environments.IgnoreQueryFilters()
            .FirstAsync(e => e.SpaceId == spaceId);

        var lifecycle = new Lifecycle
        {
            SpaceId = spaceId,
            Name    = "rc-lc",
            Phases  = [new LifecyclePhase
            {
                Name                     = "P",
                EnvironmentIds           = [env.Id],
                RetentionKeepDeployments = keep,
            }],
        };
        db.Lifecycles.Add(lifecycle);
        await db.SaveChangesAsync();

        var project = new Project
        {
            SpaceId        = spaceId,
            Name           = $"p{Guid.NewGuid():N}"[..10],
            Slug           = $"p{Guid.NewGuid():N}"[..10],
            LifecycleId    = lifecycle.Id,
            ProjectGroupId = await TestData.EnsureProjectGroupAsync(db, spaceId),
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var release = new Release
        {
            SpaceId                    = spaceId,
            ProjectId                  = project.Id,
            Version                    = "1.0",
            ProcessSnapshot            = [],
            VariableSnapshot           = [],
            VariableSnapshotUpdatedUtc = DateTimeOffset.UtcNow,
        };
        db.Releases.Add(release);
        await db.SaveChangesAsync();

        return (env.Id, release.Id);
    }

    private static async Task<(Guid EnvId, Guid RunbookId)> SeedRunbookGraphAsync(
        KrakenDbContext db, Guid spaceId)
    {
        await SeedSpaceAndEnvAsync(db, spaceId);
        var env = await db.Environments.IgnoreQueryFilters()
            .FirstAsync(e => e.SpaceId == spaceId);

        var project = new Project
        {
            SpaceId        = spaceId,
            Name           = $"p{Guid.NewGuid():N}"[..10],
            Slug           = $"p{Guid.NewGuid():N}"[..10],
            ProjectGroupId = await TestData.EnsureProjectGroupAsync(db, spaceId),
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var runbook = new Runbook { SpaceId = spaceId, ProjectId = project.Id, Name = "rc-rb" };
        db.Runbooks.Add(runbook);
        await db.SaveChangesAsync();

        return (env.Id, runbook.Id);
    }

    private static async Task SeedSpaceAndEnvAsync(KrakenDbContext db, Guid spaceId)
    {
        db.Spaces.Add(new Space
        {
            Id = spaceId, Slug = $"rc-{spaceId:N}"[..12], Name = "Retention cascade",
        });
        db.Environments.Add(new DeploymentEnvironment
        {
            SpaceId   = spaceId,
            Name      = $"e{Guid.NewGuid():N}"[..10],
            Slug      = $"e{Guid.NewGuid():N}"[..10],
            SortOrder = 1,
        });
        await db.SaveChangesAsync();
    }

    private static Deployment NewDeployment(
        Guid spaceId, Guid releaseId, Guid envId, DateTimeOffset? completedUtc)
        => new()
        {
            SpaceId       = spaceId,
            ReleaseId     = releaseId,
            EnvironmentId = envId,
            Status        = DeploymentStatus.Succeeded,
            CompletedUtc  = completedUtc,
        };

    private static RunbookRun NewRunbookRun(
        Guid spaceId, Guid runbookId, Guid envId, DateTimeOffset completedUtc)
        => new()
        {
            SpaceId       = spaceId,
            RunbookId     = runbookId,
            EnvironmentId = envId,
            Status        = DeploymentStatus.Succeeded,
            CompletedUtc  = completedUtc,
        };

    /// <summary>One step-log blob + one live-log line, both keyed by the task id
    /// (neither child is ISpaceScoped — scope inherits through TaskId).</summary>
    private static void AddLogChildren(KrakenDbContext db, Guid taskId)
    {
        db.TaskStepLogs.Add(new TaskStepLog
        {
            TaskId       = taskId,
            StepIndex    = 0,
            Content      = "0|2026-01-01T00:00:00Z|info|line",
            LineCount    = 1,
            ByteSize     = 32,
            CompletedUtc = DateTimeOffset.UtcNow,
        });
        db.TaskLogLive.Add(new TaskLogLiveEntry
        {
            TaskId    = taskId,
            StepIndex = 0,
            Sequence  = 0,
            Level     = "info",
            Timestamp = DateTimeOffset.UtcNow,
            Message   = "live line",
        });
    }
}
