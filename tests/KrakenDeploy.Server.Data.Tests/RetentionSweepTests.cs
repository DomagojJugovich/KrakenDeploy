using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Lifecycles;
using KrakenDeploy.Server.Core.Domain.Packages;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Runbooks;
using KrakenDeploy.Server.Core.Domain.Spaces;
using KrakenDeploy.Server.Data.ArtifactStorage;
using KrakenDeploy.Server.Data.Accounts;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// WP9 retention-expansion tests for <see cref="RetentionService.RunSweepAsync"/>:
/// release pruning (Octopus keep-window + deployment-reference guard),
/// reference-protected package pruning, per-runbook run-keep override, step-log
/// age-capping, on-disk file cleanup (inline + orphan safety-net sweep), and the
/// dry-run contract (accurate counts, zero deletes). Docker/Postgres-gated + real
/// DI so the scoped <see cref="ISpaceContext"/> flows into the query filter and the
/// <c>ExecuteDelete</c> cascades are exercised against real Postgres.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class RetentionSweepTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    // ── Release pruning ─────────────────────────────────────────────────────

    [Fact]
    public async Task Sweep_prunes_unreferenced_releases_outside_the_keep_window()
    {
        var spaceId = Guid.NewGuid();
        Guid oldRelease, midRelease, newRelease;
        await using (var db = postgres.CreateContext())
        {
            var (envId, _) = await SeedGraphAsync(db, spaceId, keepReleases: 1);
            var project = await db.Projects.IgnoreQueryFilters()
                .FirstAsync(p => p.SpaceId == spaceId);

            // Three releases, oldest first. keep=1 → the two oldest fall outside the
            // window; only those with NO deployment are prunable.
            oldRelease = await SeedReleaseAsync(db, spaceId, project.Id, "1.0", ageHours: 3);
            midRelease = await SeedReleaseAsync(db, spaceId, project.Id, "2.0", ageHours: 2);
            newRelease = await SeedReleaseAsync(db, spaceId, project.Id, "3.0", ageHours: 1);

            // Pin the newest release with a deployment (execution history is
            // delete-proof) — it must survive even though it is inside the window.
            db.Deployments.Add(NewDeployment(spaceId, newRelease, envId, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var result = await RunSweepAsync(
            new RetentionSweepOptions { DryRun = false });

        await using var check = postgres.CreateContext();
        (await check.Releases.IgnoreQueryFilters().AnyAsync(r => r.Id == oldRelease))
            .Should().BeFalse("the oldest unreferenced release is outside keep=1 and has no deployments");
        (await check.Releases.IgnoreQueryFilters().AnyAsync(r => r.Id == midRelease))
            .Should().BeFalse("the second-oldest unreferenced release is outside keep=1");
        (await check.Releases.IgnoreQueryFilters().AnyAsync(r => r.Id == newRelease))
            .Should().BeTrue("the newest release is inside the keep window AND pinned by a deployment");
        result.Releases.Should().BeGreaterThanOrEqualTo(2,
            "at least this Space's two unreferenced releases are pruned (the count is global across the shared test DB)");
    }

    [Fact]
    public async Task Sweep_never_prunes_a_release_referenced_by_a_deployment()
    {
        // The "no retained deployments" half of the Octopus rule: even a release far
        // outside the keep window survives while any deployment references it (the
        // RESTRICT FK would refuse the delete anyway — the sweep pre-filters).
        var spaceId = Guid.NewGuid();
        Guid referencedRelease, freshRelease;
        await using (var db = postgres.CreateContext())
        {
            var (envId, _) = await SeedGraphAsync(db, spaceId, keepReleases: 1);
            var project = await db.Projects.IgnoreQueryFilters()
                .FirstAsync(p => p.SpaceId == spaceId);

            referencedRelease = await SeedReleaseAsync(db, spaceId, project.Id, "1.0", ageHours: 5);
            freshRelease      = await SeedReleaseAsync(db, spaceId, project.Id, "2.0", ageHours: 1);

            // Reference the OLD release with a deployment — it must be protected.
            db.Deployments.Add(NewDeployment(spaceId, referencedRelease, envId, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        await RunSweepAsync(new RetentionSweepOptions { DryRun = false });

        await using var check = postgres.CreateContext();
        (await check.Releases.IgnoreQueryFilters().AnyAsync(r => r.Id == referencedRelease))
            .Should().BeTrue("a release with a deployment is pinned by execution history and must survive");
        (await check.Releases.IgnoreQueryFilters().AnyAsync(r => r.Id == freshRelease))
            .Should().BeTrue("the newest release is inside the keep window");
    }

    [Fact]
    public async Task Sweep_skips_release_pruning_when_keep_is_zero()
    {
        var spaceId = Guid.NewGuid();
        Guid r1, r2;
        await using (var db = postgres.CreateContext())
        {
            await SeedGraphAsync(db, spaceId, keepReleases: 0);   // 0 = disabled
            var project = await db.Projects.IgnoreQueryFilters()
                .FirstAsync(p => p.SpaceId == spaceId);
            r1 = await SeedReleaseAsync(db, spaceId, project.Id, "1.0", ageHours: 3);
            r2 = await SeedReleaseAsync(db, spaceId, project.Id, "2.0", ageHours: 1);
            await db.SaveChangesAsync();
        }

        var result = await RunSweepAsync(new RetentionSweepOptions { DryRun = false });

        await using var check = postgres.CreateContext();
        (await check.Releases.IgnoreQueryFilters().CountAsync(r => r.Id == r1 || r.Id == r2))
            .Should().Be(2, "keep=0 disables release pruning (opt-in, like deployments)");
        result.Releases.Should().Be(0);
    }

    // ── Package pruning + reference protection ────────────────────────────────

    [Fact]
    public async Task Sweep_prunes_old_package_versions_but_protects_a_retained_release_pin()
    {
        var spaceId = Guid.NewGuid();
        await using (var db = postgres.CreateContext())
        {
            var (envId, _) = await SeedGraphAsync(db, spaceId, keepReleases: 0);
            var project = await db.Projects.IgnoreQueryFilters()
                .FirstAsync(p => p.SpaceId == spaceId);

            // A protected release (has a deployment) whose snapshot pins app@1.0.0.
            var release = await SeedReleaseAsync(db, spaceId, project.Id, "1.0", ageHours: 1,
                snapshot: [NewStepSnapshot("app", "1.0.0")]);
            db.Deployments.Add(NewDeployment(spaceId, release, envId, DateTimeOffset.UtcNow));

            // app has two versions: 1.0.0 (older, PINNED) and 2.0.0 (newest).
            await SeedPackageAsync(db, spaceId, "app", "1.0.0", ageHours: 2);
            await SeedPackageAsync(db, spaceId, "app", "2.0.0", ageHours: 1);

            // other has two versions, neither pinned → keep=1 prunes the older.
            await SeedPackageAsync(db, spaceId, "other", "1.0.0", ageHours: 2);
            await SeedPackageAsync(db, spaceId, "other", "2.0.0", ageHours: 1);
            await db.SaveChangesAsync();
        }

        var result = await RunSweepAsync(
            new RetentionSweepOptions { PackageKeepVersions = 1, DryRun = false });

        await using var check = postgres.CreateContext();
        var app = await check.Packages.IgnoreQueryFilters()
            .Where(p => p.SpaceId == spaceId && p.PackageId == "app").Select(p => p.Version).ToListAsync();
        var other = await check.Packages.IgnoreQueryFilters()
            .Where(p => p.SpaceId == spaceId && p.PackageId == "other").Select(p => p.Version).ToListAsync();

        app.Should().BeEquivalentTo(["1.0.0", "2.0.0"],
            "1.0.0 is older than the keep=1 window but pinned by a retained release's snapshot — the reference guard wins");
        other.Should().BeEquivalentTo(["2.0.0"],
            "the unpinned older version of an unpinned package is pruned to the newest");
        result.Packages.Should().BeGreaterThanOrEqualTo(1,
            "at least this Space's unpinned older version is pruned (the count is global across the shared test DB)");
    }

    [Fact]
    public async Task Sweep_package_pruning_disabled_when_keep_is_zero()
    {
        var spaceId = Guid.NewGuid();
        await using (var db = postgres.CreateContext())
        {
            await SeedGraphAsync(db, spaceId, keepReleases: 0);
            await SeedPackageAsync(db, spaceId, "app", "1.0.0", ageHours: 2);
            await SeedPackageAsync(db, spaceId, "app", "2.0.0", ageHours: 1);
            await db.SaveChangesAsync();
        }

        var result = await RunSweepAsync(
            new RetentionSweepOptions { PackageKeepVersions = 0, DryRun = false });

        await using var check = postgres.CreateContext();
        (await check.Packages.IgnoreQueryFilters().CountAsync(p => p.PackageId == "app"))
            .Should().Be(2, "keep=0 disables package pruning");
        result.Packages.Should().Be(0);
    }

    [Fact]
    public async Task Sweep_protects_all_versions_when_package_reference_is_unresolved()
    {
        var spaceId = Guid.NewGuid();
        await using (var db = postgres.CreateContext())
        {
            var (envId, _) = await SeedGraphAsync(db, spaceId, keepReleases: 0);
            var project = await db.Projects.IgnoreQueryFilters()
                .FirstAsync(p => p.SpaceId == spaceId);

            // A protected release (has a deployment) whose snapshot references "helpers" with NO resolved version
            var release = await SeedReleaseAsync(db, spaceId, project.Id, "1.0", ageHours: 1,
                snapshot: [NewStepSnapshotWithUnresolvedReference("app", "1.0.0", "helpers")]);
            db.Deployments.Add(NewDeployment(spaceId, release, envId, DateTimeOffset.UtcNow));

            // helpers has three versions — ALL must be protected because the reference is unresolved
            await SeedPackageAsync(db, spaceId, "helpers", "1.0.0", ageHours: 3);
            await SeedPackageAsync(db, spaceId, "helpers", "2.0.0", ageHours: 2);
            await SeedPackageAsync(db, spaceId, "helpers", "3.0.0", ageHours: 1);
            await db.SaveChangesAsync();
        }

        var result = await RunSweepAsync(
            new RetentionSweepOptions { PackageKeepVersions = 1, DryRun = false });

        await using var check = postgres.CreateContext();
        var helpers = await check.Packages.IgnoreQueryFilters()
            .Where(p => p.SpaceId == spaceId && p.PackageId == "helpers")
            .Select(p => p.Version)
            .ToListAsync();

        helpers.Should().BeEquivalentTo(["1.0.0", "2.0.0", "3.0.0"],
            "an unresolved package reference (empty version) protects ALL versions of that package ID");
        result.Packages.Should().Be(0, "no packages should be pruned when all are protected");
    }

    // ── Runbook-run keep override ─────────────────────────────────────────────

    [Fact]
    public async Task Sweep_honours_the_per_runbook_keep_override()
    {
        var spaceId = Guid.NewGuid();
        Guid oldRun, newRun;
        await using (var db = postgres.CreateContext())
        {
            var (envId, runbookId) = await SeedRunbookGraphAsync(db, spaceId, keepRuns: 1);
            var baseUtc = DateTimeOffset.UtcNow;
            var o = NewRunbookRun(spaceId, runbookId, envId, baseUtc.AddHours(-1));
            var n = NewRunbookRun(spaceId, runbookId, envId, baseUtc);
            db.RunbookRuns.AddRange(o, n);
            await db.SaveChangesAsync();
            oldRun = o.Id;
            newRun = n.Id;
        }

        // Instance default is high (99) — the per-runbook override (1) must win.
        var result = await RunSweepAsync(
            new RetentionSweepOptions { RunbookRunKeep = 99, DryRun = false });

        await using var check = postgres.CreateContext();
        (await check.RunbookRuns.IgnoreQueryFilters().AnyAsync(r => r.Id == oldRun))
            .Should().BeFalse("the per-runbook keep=1 override prunes the older run despite the instance default of 99");
        (await check.RunbookRuns.IgnoreQueryFilters().AnyAsync(r => r.Id == newRun))
            .Should().BeTrue("the newest run is retained");
        result.RunbookRuns.Should().Be(1);
    }

    // ── Step-log age cap + orphan live-log sweep ───────────────────────────────

    [Fact]
    public async Task Sweep_age_caps_step_logs_and_sweeps_orphaned_live_logs()
    {
        var spaceId = Guid.NewGuid();
        Guid oldTask, liveOrphanTask;
        await using (var db = postgres.CreateContext())
        {
            var (envId, releaseId) = await SeedGraphWithReleaseAsync(db, spaceId, keepReleases: 0);

            // A completed deployment from 10 days ago with a step-log blob.
            var old = NewDeployment(spaceId, releaseId, envId, DateTimeOffset.UtcNow.AddDays(-10));
            db.Deployments.Add(old);
            await db.SaveChangesAsync();
            db.TaskStepLogs.Add(new TaskStepLog
            {
                TaskId = old.Id, StepIndex = 0, Content = "0|2026-01-01T00:00:00Z|info|x",
                LineCount = 1, ByteSize = 8, CompletedUtc = DateTimeOffset.UtcNow.AddDays(-10),
            });

            // A terminal deployment that still has a live-log row (an orphan — the
            // compactor should have swept it at terminal status). Backdate completion
            // by 2 hours so it falls outside the 1-hour compactor grace period.
            var orphan = NewDeployment(spaceId, releaseId, envId, DateTimeOffset.UtcNow.AddHours(-2));
            db.Deployments.Add(orphan);
            await db.SaveChangesAsync();
            db.TaskLogLive.Add(new TaskLogLiveEntry
            {
                TaskId = orphan.Id, StepIndex = 0, Sequence = 0, Level = "info",
                Timestamp = DateTimeOffset.UtcNow.AddHours(-2), Message = "straggler",
            });
            await db.SaveChangesAsync();
            oldTask = old.Id;
            liveOrphanTask = orphan.Id;
        }

        var result = await RunSweepAsync(
            new RetentionSweepOptions { TaskLogAgeDays = 5, DryRun = false });

        await using var check = postgres.CreateContext();
        (await check.TaskStepLogs.AnyAsync(l => l.TaskId == oldTask))
            .Should().BeFalse("the 10-day-old step-log blob exceeds the 5-day age cap");
        (await check.Deployments.IgnoreQueryFilters().AnyAsync(d => d.Id == oldTask))
            .Should().BeTrue("the age cap prunes the LOG, not the deployment row");
        (await check.TaskLogLive.AnyAsync(l => l.TaskId == liveOrphanTask))
            .Should().BeFalse("a live-log row on a terminal task is an orphan and is swept");
        result.StepLogBlobs.Should().Be(1);
        result.OrphanLiveLogs.Should().Be(1);
    }

    // ── Dry-run contract ──────────────────────────────────────────────────────

    [Fact]
    public async Task DryRun_reports_counts_but_deletes_nothing()
    {
        var spaceId = Guid.NewGuid();
        await using (var db = postgres.CreateContext())
        {
            var (envId, _) = await SeedGraphAsync(db, spaceId, keepReleases: 1);
            var project = await db.Projects.IgnoreQueryFilters()
                .FirstAsync(p => p.SpaceId == spaceId);

            // Two unreferenced releases outside keep=1 → would be pruned.
            await SeedReleaseAsync(db, spaceId, project.Id, "1.0", ageHours: 3);
            await SeedReleaseAsync(db, spaceId, project.Id, "2.0", ageHours: 2);
            await SeedReleaseAsync(db, spaceId, project.Id, "3.0", ageHours: 1);

            // Two package versions, keep=1 → one would be pruned.
            await SeedPackageAsync(db, spaceId, "app", "1.0.0", ageHours: 2);
            await SeedPackageAsync(db, spaceId, "app", "2.0.0", ageHours: 1);
            await db.SaveChangesAsync();
        }

        var before = await SnapshotCountsAsync();
        var result = await RunSweepAsync(
            new RetentionSweepOptions { PackageKeepVersions = 1, DryRun = true });
        var after = await SnapshotCountsAsync();

        result.DryRun.Should().BeTrue();
        result.Releases.Should().BeGreaterThanOrEqualTo(2,
            "dry-run still computes the release prune set accurately (floor — the count is global across the shared test DB)");
        result.Packages.Should().BeGreaterThanOrEqualTo(1,
            "dry-run still computes the package prune set accurately (floor — the count is global across the shared test DB)");
        after.Should().BeEquivalentTo(before,
            "dry-run must not delete a single row — the counts are a preview only");
    }

    // ── On-disk file cleanup ──────────────────────────────────────────────────

    [Fact]
    public async Task Sweep_deletes_inline_and_orphaned_files_on_disk()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), $"kraken-sweep-{Guid.NewGuid():N}");
        var spaceId = Guid.NewGuid();
        Guid prunedDeployment;
        string inlineArtifactRel, orphanArtifactDir, orphanBundleDir;
        try
        {
            var store = new LocalArtifactStore(dataPath, new DisabledAccountContext());

            await using (var db = postgres.CreateContext())
            {
                var (envId, releaseId) = await SeedGraphWithReleaseAsync(db, spaceId, keepReleases: 0,
                    deploymentKeep: 1);

                // Two successful deployments; keep=1 prunes the older. Give the older
                // one an artifact file on disk so inline deletion has something to remove.
                var baseUtc = DateTimeOffset.UtcNow;
                var pruned = NewDeployment(spaceId, releaseId, envId, baseUtc.AddHours(-1));
                var kept   = NewDeployment(spaceId, releaseId, envId, baseUtc);
                db.Deployments.AddRange(pruned, kept);
                await db.SaveChangesAsync();

                inlineArtifactRel = await store.SaveAsync(
                    pruned.Id, "step", "out.txt",
                    new MemoryStream("payload"u8.ToArray()));
                db.TaskArtifacts.Add(new TaskArtifact
                {
                    SpaceId = spaceId, TaskId = pruned.Id, StepName = "step",
                    FileName = "out.txt", StoredPath = inlineArtifactRel,
                    SizeBytes = 7, CollectedUtc = baseUtc,
                });
                await db.SaveChangesAsync();
                prunedDeployment = pruned.Id;
            }

            // Orphans on disk with NO owning row: an artifact dir + a drop-bundle dir.
            var artifactRoot = Path.Combine(dataPath, "artifacts");
            orphanArtifactDir = Path.Combine(artifactRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(orphanArtifactDir);
            await File.WriteAllTextAsync(Path.Combine(orphanArtifactDir, "x.txt"), "orphan");

            orphanBundleDir = Path.Combine(dataPath, "drop-bundles", Guid.NewGuid().ToString());
            Directory.CreateDirectory(orphanBundleDir);
            await File.WriteAllTextAsync(Path.Combine(orphanBundleDir, "drop.zip"), "orphan");

            var inlineFullPath = Path.Combine(dataPath, "artifacts", inlineArtifactRel.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(inlineFullPath).Should().BeTrue("precondition: the inline artifact exists on disk");

            // The sweep's DI-registered LocalArtifactStore roots at the SAME dataPath
            // as the seeding store above, so it resolves + deletes the same file.
            var result = await RunSweepAsync(
                new RetentionSweepOptions { DryRun = false }, dataPath: dataPath);

            File.Exists(inlineFullPath).Should().BeFalse(
                "the pruned deployment's artifact file is deleted inline with its row");
            Directory.Exists(orphanArtifactDir).Should().BeFalse(
                "an artifact dir with no owning task row is swept as an orphan");
            Directory.Exists(orphanBundleDir).Should().BeFalse(
                "a drop-bundle dir not referenced by any task is swept as an orphan");
            result.ArtifactFiles.Should().BeGreaterThanOrEqualTo(2,
                "one inline artifact + at least one orphaned artifact dir");
            result.DropBundleFiles.Should().BeGreaterThanOrEqualTo(1);

            await using var check = postgres.CreateContext();
            (await check.Deployments.IgnoreQueryFilters().AnyAsync(d => d.Id == prunedDeployment))
                .Should().BeFalse("the older deployment is pruned by keep=1");
        }
        finally
        {
            try
            {
                if (Directory.Exists(dataPath))
                {
                    Directory.Delete(dataPath, recursive: true);
                }
            }
            catch
            {
                // best effort
            }
        }
    }

    // ── Contract guard: terminal-success is not narrowed ──────────────────────

    [Fact]
    public async Task Sweep_counts_SucceededWithWarnings_as_a_terminal_success()
    {
        // The settled contract (see RetentionService): the prune candidate set spans
        // BOTH Succeeded and SucceededWithWarnings. This guards the WP9 sweep against
        // narrowing it back to Succeeded-only — a warning-status deployment must count
        // toward the keep window AND be prunable.
        var spaceId = Guid.NewGuid();
        Guid warnId, succId;
        await using (var db = postgres.CreateContext())
        {
            var (envId, releaseId) = await SeedGraphWithReleaseAsync(db, spaceId, keepReleases: 0,
                deploymentKeep: 1);
            var baseUtc = DateTimeOffset.UtcNow;
            var warn = NewDeployment(spaceId, releaseId, envId, baseUtc.AddHours(-1));
            warn.Status = DeploymentStatus.SucceededWithWarnings;
            var succ = NewDeployment(spaceId, releaseId, envId, baseUtc);
            db.Deployments.AddRange(warn, succ);
            await db.SaveChangesAsync();
            warnId = warn.Id;
            succId = succ.Id;
        }

        await RunSweepAsync(new RetentionSweepOptions { DryRun = false });

        await using var check = postgres.CreateContext();
        (await check.Deployments.IgnoreQueryFilters().AnyAsync(d => d.Id == warnId))
            .Should().BeFalse("a SucceededWithWarnings deployment beyond keep=1 is pruned (not invisible to retention)");
        (await check.Deployments.IgnoreQueryFilters().AnyAsync(d => d.Id == succId))
            .Should().BeTrue("the newest success is retained");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the sweep through the REAL DI container (<c>AddKrakenDeployData</c>) so the
    /// scoped <see cref="ISpaceContext"/> is shared between <see cref="RetentionService"/>
    /// and its factory's <c>KrakenDbContext</c> — the sweep's per-Space
    /// <c>WithSpace</c> must flow into the query filter, exactly as in production. A
    /// bare fixture factory news up a fresh context per call and would NOT reproduce
    /// that wiring (the sweep would see only Default-Space rows). <paramref name="dataPath"/>
    /// flows to the registered stores + config so the file-cleanup test can assert on
    /// a known on-disk root.
    /// </summary>
    private async Task<RetentionSweepResult> RunSweepAsync(
        RetentionSweepOptions options, string? dataPath = null)
    {
        var services = new ServiceCollection();
        services.AddKrakenDeployData(
            postgres.ConnectionString,
            dataPath ?? Path.Combine(Path.GetTempPath(), $"kraken-sw-{Guid.NewGuid():N}"));
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<RetentionService>()
            .RunSweepAsync(options);
    }

    private async Task<Dictionary<string, int>> SnapshotCountsAsync()
    {
        await using var db = postgres.CreateContext();
        return new Dictionary<string, int>
        {
            ["releases"]  = await db.Releases.IgnoreQueryFilters().CountAsync(),
            ["packages"]  = await db.Packages.IgnoreQueryFilters().CountAsync(),
            ["deploys"]   = await db.Deployments.IgnoreQueryFilters().CountAsync(),
            ["runs"]      = await db.RunbookRuns.IgnoreQueryFilters().CountAsync(),
            ["steplogs"]  = await db.TaskStepLogs.CountAsync(),
            ["livelogs"]  = await db.TaskLogLive.CountAsync(),
        };
    }

    /// <summary>Seeds the full graph PLUS one release and returns
    /// (envId, releaseId) — for tests that insert deployments (which need a real
    /// release FK target).</summary>
    private static async Task<(Guid EnvId, Guid ReleaseId)> SeedGraphWithReleaseAsync(
        KrakenDbContext db, Guid spaceId, int keepReleases, int deploymentKeep = 0)
    {
        var (envId, _) = await SeedGraphAsync(db, spaceId, keepReleases, deploymentKeep);
        var project = await db.Projects.IgnoreQueryFilters()
            .FirstAsync(p => p.SpaceId == spaceId);
        var releaseId = await SeedReleaseAsync(db, spaceId, project.Id, "1.0", ageHours: 1);
        return (envId, releaseId);
    }

    /// <summary>Seeds Space + Environment + Lifecycle(+phase) + Project. Returns the
    /// environment id and the project's lifecycle id.</summary>
    private static async Task<(Guid EnvId, Guid LifecycleId)> SeedGraphAsync(
        KrakenDbContext db, Guid spaceId, int keepReleases, int deploymentKeep = 0)
    {
        db.Spaces.Add(new Space
        {
            Id = spaceId, Slug = $"sw-{spaceId:N}"[..12], Name = "Sweep",
        });
        var env = new DeploymentEnvironment
        {
            SpaceId = spaceId, Name = $"e{Guid.NewGuid():N}"[..10],
            Slug = $"e{Guid.NewGuid():N}"[..10], SortOrder = 1,
        };
        db.Environments.Add(env);
        await db.SaveChangesAsync();

        var lifecycle = new Lifecycle
        {
            SpaceId = spaceId,
            Name    = "sw-lc",
            Phases  = [new LifecyclePhase
            {
                Name                     = "Prod",
                EnvironmentIds           = [env.Id],
                RetentionKeepDeployments = deploymentKeep,
                RetentionKeepReleases    = keepReleases,
            }],
        };
        db.Lifecycles.Add(lifecycle);
        await db.SaveChangesAsync();

        db.Projects.Add(new Project
        {
            SpaceId        = spaceId,
            Name           = $"p{Guid.NewGuid():N}"[..10],
            Slug           = $"p{Guid.NewGuid():N}"[..10],
            LifecycleId    = lifecycle.Id,
            ProjectGroupId = await TestData.EnsureProjectGroupAsync(db, spaceId),
        });
        await db.SaveChangesAsync();
        return (env.Id, lifecycle.Id);
    }

    private static async Task<(Guid EnvId, Guid RunbookId)> SeedRunbookGraphAsync(
        KrakenDbContext db, Guid spaceId, int keepRuns)
    {
        db.Spaces.Add(new Space
        {
            Id = spaceId, Slug = $"rb-{spaceId:N}"[..12], Name = "Runbook sweep",
        });
        var env = new DeploymentEnvironment
        {
            SpaceId = spaceId, Name = $"e{Guid.NewGuid():N}"[..10],
            Slug = $"e{Guid.NewGuid():N}"[..10], SortOrder = 1,
        };
        db.Environments.Add(env);
        await db.SaveChangesAsync();

        var project = new Project
        {
            SpaceId        = spaceId,
            Name           = $"p{Guid.NewGuid():N}"[..10],
            Slug           = $"p{Guid.NewGuid():N}"[..10],
            ProjectGroupId = await TestData.EnsureProjectGroupAsync(db, spaceId),
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var runbook = new Runbook
        {
            SpaceId = spaceId, ProjectId = project.Id, Name = "sw-rb",
            RetentionKeepRuns = keepRuns,
        };
        db.Runbooks.Add(runbook);
        await db.SaveChangesAsync();
        return (env.Id, runbook.Id);
    }

    private static async Task<Guid> SeedReleaseAsync(
        KrakenDbContext db, Guid spaceId, Guid projectId, string version,
        int ageHours, List<StepSnapshot>? snapshot = null)
    {
        var release = new Release
        {
            SpaceId                    = spaceId,
            ProjectId                  = projectId,
            Version                    = version,
            ProcessSnapshot            = snapshot ?? [],
            VariableSnapshot           = [],
            VariableSnapshotUpdatedUtc = DateTimeOffset.UtcNow,
        };
        db.Releases.Add(release);
        await db.SaveChangesAsync();

        // Backdate CreatedUtc so the newest-first keep-window ordering is deterministic
        // (the interceptor stamps CreatedUtc=now on insert).
        await db.Releases.IgnoreQueryFilters()
            .Where(r => r.Id == release.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.CreatedUtc, DateTimeOffset.UtcNow.AddHours(-ageHours)));
        return release.Id;
    }

    private static async Task SeedPackageAsync(
        KrakenDbContext db, Guid spaceId, string packageId, string version, int ageHours)
    {
        db.Packages.Add(new Package
        {
            SpaceId     = spaceId,
            PackageId   = packageId,
            Version     = version,
            FileName    = $"{packageId}.{version}.nupkg",
            StoredPath  = $"{packageId}/{version}/{packageId}.{version}.nupkg",
            SizeBytes   = 100,
            UploadedUtc = DateTimeOffset.UtcNow.AddHours(-ageHours),
        });
        await db.SaveChangesAsync();
    }

    private static StepSnapshot NewStepSnapshot(string packageId, string packageVersion) => new()
    {
        Name           = "deploy",
        StepType       = "Octopus.DeployPackage",
        PackageId      = packageId,
        PackageVersion = packageVersion,
    };

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

    private static StepSnapshot NewStepSnapshotWithUnresolvedReference(
        string primaryPackageId, string primaryVersion, string referencedPackageId)
    {
        var packageRefs = new[]
        {
            new { Name = "helper", PackageId = referencedPackageId, Version = "", Extract = true }
        };
        var configJson = System.Text.Json.JsonSerializer.Serialize(packageRefs);
        return new StepSnapshot
        {
            Name           = "deploy",
            StepType       = "Octopus.DeployPackage",
            PackageId      = primaryPackageId,
            PackageVersion = primaryVersion,
            Config         = new Dictionary<string, string>
            {
                [KrakenDeploy.Contracts.Steps.KrakenScriptConfigKeys.PackageReferences] = configJson
            }
        };
    }
}
