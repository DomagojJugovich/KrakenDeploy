using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Platform;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Core.Domain.Variables;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Encryption;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace KrakenDeploy.Server.Commands;

/// <summary>
/// DEV-ONLY throwaway: populates the active (Default) Space with realistic
/// sample targets, projects/releases and deployments (varied statuses + log
/// lines + step outcomes) so the redesigned screens can be reviewed with data.
/// Idempotent-ish: skips entities that already exist. Run:
///   dotnet run --project src/KrakenDeploy.Server -- seed-demo
/// Clear with: seed-demo --clear
/// </summary>
internal static class SeedDemoCommands
{
    /// <summary>
    /// Provenance marker stamped on every row this tool creates in a table it
    /// shares with real data, so <c>--clear</c> can scope its deletes: it goes
    /// into <c>CauseDetail</c> on seeded deployments and into
    /// <c>ReleaseNotes</c> on seeded releases (releases carry no other
    /// provenance column). Never change the value — already-seeded databases
    /// hold the old string and their rows would stop matching.
    /// </summary>
    private const string SeedMarker = "seed-demo";

    /// <summary>
    /// Stamped into the Description of projects this tool creates; --clear
    /// requires it IN ADDITION to the slug before deleting a project, so a
    /// real project that merely collides on slug is never destroyed.
    /// </summary>
    private const string DemoDescriptionSuffix = " — demo project";

    /// <summary>
    /// The demo projects, single source of truth for seed AND clear. The
    /// seeder reuses an existing project with a matching slug instead of
    /// creating one, so by its own semantics a matching slug IS the demo
    /// project — <c>--clear</c> deletes projects by these slugs.
    /// </summary>
    private static readonly (string Name, string Slug, string Version)[] DemoProjectSpecs =
    [
        ("Argosy Web", "argosy-web", "2026.6.4"),
        ("Argosy API", "argosy-api", "2026.6.4"),
        ("Billing Service", "billing-service", "4.18.0"),
        ("Identity Provider", "identity-provider", "3.2.1"),
    ];

    public static async Task<int> RunAsync(string[] args, string contentRoot)
    {
        var clear = args.Contains("--clear");

        var builder = CliHost.CreateBuilder(contentRoot);

        // seed-demo writes fake demo data (Grad Dubrovnik, Argosy Web, demo targets, …)
        // into the Default Space. It is a dev-only single-tenant tool; under the Saas
        // topology (production SaaS) resolving a real tenant and filling it with demo
        // garbage is a footgun, so refuse outright rather than offer --account.
        var topology = CliHost.ResolveTopologyOrError(builder.Configuration);
        if (topology is null)
        {
            return 1;
        }

        if (topology == DeploymentTopology.Saas)
        {
            Console.Error.WriteLine(
                "seed-demo is a dev-only single-tenant tool and is not supported under " +
                "Deployment:Topology=Saas (it would write demo data into a real tenant database).");
            return 1;
        }

        var connectionString = builder.Configuration.GetConnectionString("KrakenDb");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine(
                "ConnectionStrings:KrakenDb is not configured. " +
                "Set it in appsettings.{Environment}.json or via env var.");
            return 1;
        }

        builder.Services.AddKrakenDeployData(connectionString);
        // seed-demo DOES create sensitive variables → it must have a real DEK.
        // Use the configured KEK if present, else a random one. seed-demo is a
        // DEV-ONLY throwaway, so an ephemeral KEK is acceptable — but warn, since
        // the DEK it provisions is unrecoverable once this process exits.
        var seedKek = builder.Configuration["Encryption:MasterKey"];
        if (string.IsNullOrWhiteSpace(seedKek))
        {
            seedKek = Convert.ToBase64String(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            Console.WriteLine(
                "WARNING: Encryption:MasterKey not set — using an ephemeral KEK. The provisioned DEK " +
                "is unrecoverable after this process exits (fine for a throwaway demo DB).");
        }
        builder.Services.AddKrakenDeployEncryption(seedKek);

        using var host = builder.Build();
        await using var scope = host.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var db = sp.GetRequiredService<KrakenDbContext>();
        await db.Database.MigrateAsync().ConfigureAwait(false);
        await sp.GetRequiredService<IDekProvider>().EnsureDekAsync().ConfigureAwait(false);
        await sp.GetRequiredService<SpaceService>().EnsureDefaultAsync().ConfigureAwait(false);

        var dbFactory = sp.GetRequiredService<IDbContextFactory<KrakenDbContext>>();

        if (clear)
        {
            await ClearAsync(dbFactory).ConfigureAwait(false);
            Console.WriteLine(
                "Demo data cleared: demo projects (releases, deployments, runbooks, processes), " +
                "seed-marked releases/deployments on other projects, demo tenants, demo targets.");
            return 0;
        }

        var projectSvc = sp.GetRequiredService<ProjectService>();
        var processSvc = sp.GetRequiredService<ProcessService>();
        var releaseSvc = sp.GetRequiredService<ReleaseService>();
        var envSvc = sp.GetRequiredService<EnvironmentService>();

        // ── Targets ──────────────────────────────────────────────────────
        await SeedTargetsAsync(dbFactory).ConfigureAwait(false);
        var targets = await (await dbFactory.CreateDbContextAsync()).DeploymentTargets.AsNoTracking().ToListAsync();

        // ── Environments (use existing; ensure at least Dev/Test/Stage/Prod) ─
        var envs = await envSvc.GetAllOrderedAsync().ConfigureAwait(false);
        if (envs.Count == 0)
        {
            await envSvc.CreateAsync("Development", "development").ConfigureAwait(false);
            await envSvc.CreateAsync("Test", "test").ConfigureAwait(false);
            await envSvc.CreateAsync("Staging", "staging").ConfigureAwait(false);
            await envSvc.CreateAsync("Production", "production").ConfigureAwait(false);
            envs = await envSvc.GetAllOrderedAsync().ConfigureAwait(false);
        }

        // ── Projects + process + releases ─────────────────────────────────
        var releases = new List<(Guid ReleaseId, string Project, string Version)>();
        foreach (var (name, slug, version) in DemoProjectSpecs)
        {
            await using var pdb = await dbFactory.CreateDbContextAsync();
            var project = await pdb.Projects.FirstOrDefaultAsync(p => p.Slug == slug);
            if (project is null)
            {
                project = await projectSvc.CreateAsync(name, slug, $"{name}{DemoDescriptionSuffix}").ConfigureAwait(false);
                await processSvc.AddStepAsync(project.Id, "Deploy package to IIS", "Script", "", ["web-server"], [], KrakenDeploy.Server.Core.Domain.Security.CallerAuthorization.System).ConfigureAwait(false);
                await processSvc.AddStepAsync(project.Id, "Run database migrations", "Script", "", ["db"], [], KrakenDeploy.Server.Core.Domain.Security.CallerAuthorization.System).ConfigureAwait(false);
                await processSvc.AddStepAsync(project.Id, "Smoke test — health endpoint", "Script", "", ["web-server"], [], KrakenDeploy.Server.Core.Domain.Security.CallerAuthorization.System).ConfigureAwait(false);
            }

            await using var rdb = await dbFactory.CreateDbContextAsync();
            var release = await rdb.Releases.FirstOrDefaultAsync(r => r.ProjectId == project.Id && r.Version == version)
                          ?? await releaseSvc.CreateAsync(project.Id, version, KrakenDeploy.Server.Core.Domain.Security.CallerAuthorization.System, releaseNotes: SeedMarker).ConfigureAwait(false);
            releases.Add((release.Id, name, version));
        }

        // ── Deployments (direct insert; varied terminal + live states) ────
        await SeedDeploymentsAsync(dbFactory, releases, envs.Select(e => e.Id).ToList(), targets);

        // ── Release ladder + promotion matrix for EVERY project ───────────
        await SeedProjectMatricesAsync(dbFactory, processSvc, releaseSvc, targets).ConfigureAwait(false);

        // ── Tenants + project connections + deployment tagging ────────────
        await SeedTenantsAsync(dbFactory, sp.GetRequiredService<TenantService>()).ConfigureAwait(false);

        // ── Channels: give two projects a non-default channel and put their
        //    newest (deployed) releases on it, so channel pills render on the
        //    Projects / project dashboards ─────────────────────────────────
        await SeedChannelsAsync(dbFactory, sp.GetRequiredService<ChannelService>()).ConfigureAwait(false);

        // ── Variables: project vars (scoped + sensitive), a shared library
        //    set, and per-tenant values — so the Variables tabs (Project /
        //    Tenant / All / Preview) render populated for demo projects ─────
        await SeedVariablesAsync(dbFactory, sp.GetRequiredService<VariableService>()).ConfigureAwait(false);

        // ── Runbooks: a few per project so the project Runbooks tab has data ─
        await SeedRunbooksAsync(dbFactory, sp.GetRequiredService<RunbookService>()).ConfigureAwait(false);

        Console.WriteLine($"Seeded: {targets.Count} targets, {DemoProjectSpecs.Length} projects/releases, demo deployments, 3 tenants, variables, runbooks.");
        Console.WriteLine("Open the dashboard / deployments / targets to review. Re-run with --clear to remove.");
        return 0;
    }

    private static async Task SeedTargetsAsync(IDbContextFactory<KrakenDbContext> dbFactory)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.DeploymentTargets.AnyAsync(t => t.Name.StartsWith("demo-")))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        DeploymentTarget T(string name, string host, TargetStatus status, TargetRiskLevel risk,
            string os, string agent, TransportMode tm, List<string> roles, int seenMinAgo) => new()
        {
            SpaceId = WellKnown.DefaultSpaceId,
            Name = name,
            MachineName = host,
            Status = status,
            RiskLevel = risk,
            OperatingSystem = os,
            AgentVersion = agent,
            TransportMode = tm,
            Roles = roles,
            LastSeenUtc = now.AddMinutes(-seenMinAgo),
        };

        db.DeploymentTargets.AddRange(
            T("demo-web-prod-01", "WEB-PROD-01", TargetStatus.Online, TargetRiskLevel.Production, "Windows Server 2022", "1.4.2", TransportMode.Reverse, ["web-server"], 1),
            T("demo-web-prod-02", "WEB-PROD-02", TargetStatus.Online, TargetRiskLevel.Production, "Windows Server 2022", "1.4.0", TransportMode.Reverse, ["web-server"], 2),
            T("demo-api-prod-03", "API-PROD-03", TargetStatus.Offline, TargetRiskLevel.Production, "Windows Server 2019", "1.4.0", TransportMode.Reverse, ["api"], 240),
            T("demo-dmz-gw-01", "DMZ-GW-01", TargetStatus.Online, TargetRiskLevel.Production, "Windows Server 2022", "1.4.2", TransportMode.OfflineDrop, ["gateway"], 90),
            T("demo-web-stage-01", "WEB-STAGE-01", TargetStatus.Online, TargetRiskLevel.Staging, "Ubuntu 22.04", "1.4.2", TransportMode.Reverse, ["web-server"], 3),
            T("demo-worker-test-01", "WORKER-TEST-01", TargetStatus.Disabled, TargetRiskLevel.Development, "Ubuntu 22.04", "1.3.9", TransportMode.Reverse, ["worker"], 1440));

        await db.SaveChangesAsync();
    }

    private static async Task SeedDeploymentsAsync(
        IDbContextFactory<KrakenDbContext> dbFactory,
        List<(Guid ReleaseId, string Project, string Version)> releases,
        List<Guid> envIds,
        List<DeploymentTarget> targets)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        // Scope the double-seed guard to OUR rows: the scoped --clear leaves
        // real deployments behind, and any one of them would otherwise make a
        // re-seed silently skip the whole showcase.
        if (await db.Deployments.AnyAsync(d => d.CauseDetail == SeedMarker))
        {
            return; // don't double-seed
        }

        var prod = envIds.LastOrDefault();
        var test = envIds.FirstOrDefault();
        var stage = envIds.Count >= 2 ? envIds[^2] : test;
        var now = DateTimeOffset.UtcNow;
        var tgt = targets.FirstOrDefault();
        var rel = releases.ToDictionary(r => r.Project, r => r.ReleaseId);
        var projByName = await db.Projects.ToDictionaryAsync(p => p.Name, p => p.Id);

        Deployment Make(string project, Guid env, DeploymentStatus status, int startedMinAgo, int? durationSec, Guid? targetId)
        {
            var started = now.AddMinutes(-startedMinAgo);
            return new Deployment
            {
                SpaceId = WellKnown.DefaultSpaceId,
                ReleaseId = rel[project],
                ProjectId = projByName.GetValueOrDefault(project),
                EnvironmentId = env,
                // Target set lives exclusively in the assignments join.
                Targets = targetId is { } t
                    ? [new TaskTargetAssignment { TargetId = t, AddedUtc = now }]
                    : [],
                Status = status,
                StartedUtc = status == DeploymentStatus.Queued ? null : started,
                CompletedUtc = durationSec is { } d ? started.AddSeconds(d) : null,
                // Provenance (fix 6): seed rows are created directly (not via the
                // service), so stamp the columns inline. Demo data = CLI seed.
                Cause = ServerTaskCause.Cli,
                CreatedByDisplay = $"System ({SeedMarker})",
                CauseDetail = SeedMarker,
            };
        }

        var demoLogs = new List<TaskLogLiveEntry>();

        var running = Make("Argosy Web", prod, DeploymentStatus.Running, 4, null, tgt?.Id);
        demoLogs.AddRange(
            Log(running, 0, "═══ Step 1/3 · Deploy package to IIS ═══", "info", now.AddMinutes(-4)),
            Log(running, 1, "$ kraken deploy-package ArgosyWeb 2026.6.4", "info", now.AddMinutes(-4)),
            Log(running, 2, "Acquiring package ArgosyWeb 2026.6.4 (delta: 4.2 MB of 88 MB)", "info", now.AddMinutes(-3)),
            Log(running, 3, "web-prod-01: site re-pointed, app pool recycled", "ok", now.AddMinutes(-3)),
            Log(running, 4, "web-prod-02: agent on 1.4.0 (server is 1.4.2) — consider upgrading", "warning", now.AddMinutes(-2)),
            Log(running, 5, "═══ Step 2/3 · Run database migrations ═══", "info", now.AddMinutes(-2)),
            Log(running, 6, "Applying 2 pending migrations to DB-PROD-01", "info", now.AddMinutes(-1)));

        var failed = Make("Argosy API", prod, DeploymentStatus.Failed, 70, 90, tgt?.Id);
        demoLogs.AddRange(
            Log(failed, 0, "═══ Step 1/3 · Deploy package to IIS ═══", "info", failed.StartedUtc!.Value),
            Log(failed, 1, "api-prod-03: deploying ArgosyAPI 2026.6.4", "info", failed.StartedUtc!.Value),
            Log(failed, 2, "Smoke test — health endpoint timed out after 90s", "error", failed.StartedUtc!.Value.AddSeconds(90)));
        var failedStart = failed.StartedUtc!.Value;

        var pendingOffline = Make("Identity Provider", prod, DeploymentStatus.PendingOfflineResult, 130, null, targets.FirstOrDefault(t => t.TransportMode == TransportMode.OfflineDrop)?.Id);

        var deployments = new List<Deployment>
        {
            running,
            Make("Billing Service", stage, DeploymentStatus.Queued, 1, null, null),
            failed,
            pendingOffline,
            Make("Identity Provider", stage, DeploymentStatus.Succeeded, 90, 130, tgt?.Id),
            Make("Argosy Web", stage, DeploymentStatus.Succeeded, 160, 210, tgt?.Id),
            Make("Billing Service", test, DeploymentStatus.SucceededWithWarnings, 180, 95, tgt?.Id),
            Make("Argosy API", test, DeploymentStatus.Succeeded, 320, 140, tgt?.Id),
            Make("Argosy Web", test, DeploymentStatus.Failed, 1500, 60, tgt?.Id),
        };

        db.Deployments.AddRange(deployments);
        await db.SaveChangesAsync();

        // Seed log lines (staging rows; the detail page's stitching reader renders them).
        db.TaskLogLive.AddRange(demoLogs);

        // E-D: the next-sequence counter lives in task_log_counters now. Seed a row
        // for each task that carries pre-seeded log lines so a later append would
        // continue past them (running has 7 lines → next 7; failed has 3 → next 3).
        db.TaskLogCounters.AddRange(
            new TaskLogCounter { TaskId = running.Id, NextSequence = 7 },
            new TaskLogCounter { TaskId = failed.Id, NextSequence = 3 });

        // Step outcomes for the failed deployment (no nav collection on ServerTask).
        db.TaskStepOutcomes.AddRange(
            Outcome(failed.Id, 0, "Deploy package to IIS", StepOutcomeKind.Succeeded, failedStart, failedStart.AddSeconds(40)),
            Outcome(failed.Id, 1, "Run database migrations", StepOutcomeKind.Succeeded, failedStart.AddSeconds(40), failedStart.AddSeconds(55)),
            Outcome(failed.Id, 2, "Smoke test — health endpoint", StepOutcomeKind.TimedOut, failedStart.AddSeconds(55), failedStart.AddSeconds(90),
                "Health check did not return 200 within 90s. Last response: 503 Service Unavailable."));
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Gives EVERY project in the space a populated release × environment
    /// matrix: ensures a deployment process exists, builds a 4-step release
    /// ladder (predecessor versions of the newest release, or 2026.6.1–.4 for
    /// release-less projects), then inserts one deployment per (release, env)
    /// following a promotion story — oldest releases fully promoted, a warning
    /// and a failure in the middle, the newest mid-promotion. Idempotent: only
    /// missing releases and missing (release, env) deployments are created.
    /// </summary>
    private static async Task SeedProjectMatricesAsync(
        IDbContextFactory<KrakenDbContext> dbFactory,
        ProcessService processSvc,
        ReleaseService releaseSvc,
        List<DeploymentTarget> targets)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var projects = await db.Projects.AsNoTracking().ToListAsync();
        var envs = await db.Environments.AsNoTracking()
            .OrderBy(e => e.SortOrder).ThenBy(e => e.Name)
            .Select(e => e.Id)
            .ToListAsync();

        if (envs.Count == 0)
        {
            return;
        }

        var targetId = targets.FirstOrDefault()?.Id;
        var now = DateTimeOffset.UtcNow;

        foreach (var project in projects)
        {
            // A release requires a process with at least one step.
            var process = await processSvc.GetAsync(project.Id).ConfigureAwait(false);
            if (process is null || process.Steps.Count == 0)
            {
                await processSvc.AddStepAsync(project.Id, "Deploy package to IIS", "Script", "", ["web-server"], [], KrakenDeploy.Server.Core.Domain.Security.CallerAuthorization.System).ConfigureAwait(false);
                await processSvc.AddStepAsync(project.Id, "Run database migrations", "Script", "", ["db"], [], KrakenDeploy.Server.Core.Domain.Security.CallerAuthorization.System).ConfigureAwait(false);
                await processSvc.AddStepAsync(project.Id, "Smoke test — health endpoint", "Script", "", ["web-server"], [], KrakenDeploy.Server.Core.Domain.Security.CallerAuthorization.System).ConfigureAwait(false);
            }

            await using var pdb = await dbFactory.CreateDbContextAsync();
            var existingVersions = await pdb.Releases
                .Where(r => r.ProjectId == project.Id)
                .Select(r => r.Version)
                .ToListAsync();

            // The marker in ReleaseNotes is what lets --clear find ladder rungs
            // created on NON-demo projects (this loop runs over every project).
            foreach (var version in LadderVersions(existingVersions))
            {
                await releaseSvc.CreateAsync(project.Id, version, KrakenDeploy.Server.Core.Domain.Security.CallerAuthorization.System, releaseNotes: SeedMarker).ConfigureAwait(false);
            }

            // Ladder = up to 4 newest releases by version; promotion pattern
            // from oldest (fully promoted) to newest (first env only).
            var releases = (await pdb.Releases
                    .Where(r => r.ProjectId == project.Id)
                    .Select(r => new { r.Id, r.Version })
                    .ToListAsync())
                .OrderBy(r => r.Version, StringComparer.OrdinalIgnoreCase)
                .TakeLast(4)
                .ToList();

            var deployed = (await pdb.Deployments
                    .Where(d => d.Release.ProjectId == project.Id)
                    .Select(d => new { d.ReleaseId, d.EnvironmentId })
                    .ToListAsync())
                .Select(x => (x.ReleaseId, x.EnvironmentId))
                .ToHashSet();

            await using var ddb = await dbFactory.CreateDbContextAsync();
            for (var i = 0; i < releases.Count; i++)
            {
                var rel = releases[i];
                var ageDays = releases.Count - i; // oldest ladder rung = furthest back
                var envCount = i == releases.Count - 1 && envs.Count > 1 ? 1 : envs.Count;

                for (var e = 0; e < envCount; e++)
                {
                    if (deployed.Contains((rel.Id, envs[e])))
                    {
                        continue;
                    }

                    var status = (Rung: i, Env: e) switch
                    {
                        _ when i == releases.Count - 3 && e == envs.Count - 1 => DeploymentStatus.SucceededWithWarnings,
                        _ when i == releases.Count - 2 && e == envs.Count - 1 => DeploymentStatus.Failed,
                        _ => DeploymentStatus.Succeeded,
                    };

                    var started = now.AddDays(-ageDays).AddMinutes(e * 40);
                    ddb.Deployments.Add(new Deployment
                    {
                        SpaceId = WellKnown.DefaultSpaceId,
                        ReleaseId = rel.Id,
                        // ServerTask.ProjectId is denormalized-NOT-NULL for
                        // project-filtered reads; leaving it default persists
                        // Guid.Empty and hides the row from those filters.
                        ProjectId = project.Id,
                        EnvironmentId = envs[e],
                        Targets = targetId is { } t
                            ? [new TaskTargetAssignment { TargetId = t, AddedUtc = now }]
                            : [],
                        Status = status,
                        StartedUtc = started,
                        CompletedUtc = started.AddMinutes(3),
                        Cause = ServerTaskCause.Cli,
                        CreatedByDisplay = $"System ({SeedMarker})",
                        CauseDetail = SeedMarker,
                    });
                }
            }
            await ddb.SaveChangesAsync();
        }
    }

    /// <summary>
    /// New ladder versions to create: predecessors of the newest existing
    /// version by decrementing the last numeric segment (2026.6.4 → .3/.2/.1),
    /// or the default 2026.6.1–.4 set when the project has no releases yet.
    /// </summary>
    private static List<string> LadderVersions(List<string> existing)
    {
        if (existing.Count == 0)
        {
            return ["2026.6.1", "2026.6.2", "2026.6.3", "2026.6.4"];
        }

        var newest = existing.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).Last();
        var parts = newest.Split('.');
        if (!int.TryParse(parts[^1], out var last))
        {
            return [];
        }

        var result = new List<string>();
        for (var i = 1; i <= 3 && last - i >= 0; i++)
        {
            var candidate = string.Join('.', [.. parts[..^1], (last - i).ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            if (!existing.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(candidate);
            }
        }

        result.Reverse(); // create ascending so CreatedUtc follows version order
        return result;
    }

    private static readonly string[] DemoTenantSlugs =
        ["grad-dubrovnik", "grad-split", "ministarstvo-financija"];

    /// <summary>
    /// Demo tenants connected to the multi-tenant projects, with the seeded
    /// deployments of those projects tagged round-robin so the project
    /// tenant × environment matrix renders populated cells.
    /// </summary>
    private static async Task SeedTenantsAsync(
        IDbContextFactory<KrakenDbContext> dbFactory, TenantService tenantSvc)
    {
        (string Name, string Slug, string[] ProjectSlugs)[] specs =
        [
            // "argosy" is the hand-created test project — connected when present.
            ("Grad Dubrovnik", "grad-dubrovnik", ["argosy-web", "argosy-api", "argosy"]),
            ("Grad Split", "grad-split", ["argosy-web", "argosy-api", "argosy"]),
            ("Ministarstvo financija", "ministarstvo-financija", ["billing-service"]),
        ];

        await using var db = await dbFactory.CreateDbContextAsync();
        var projectIds = await db.Projects.ToDictionaryAsync(p => p.Slug, p => p.Id);

        foreach (var (name, slug, projectSlugs) in specs)
        {
            var tenant = await tenantSvc.GetBySlugAsync(slug).ConfigureAwait(false)
                         ?? await tenantSvc.CreateAsync(name, slug, $"{name} — demo tenant").ConfigureAwait(false);

            foreach (var ps in projectSlugs)
            {
                if (projectIds.TryGetValue(ps, out var pid))
                {
                    // ConnectProjectAsync is a no-op when already connected.
                    await tenantSvc.ConnectProjectAsync(tenant.Id, pid, KrakenDeploy.Server.Core.Domain.Security.CallerAuthorization.System).ConfigureAwait(false);
                }
            }
        }

        // Associate demo targets with tenants. The DIRECT association
        // (DeploymentTarget.Tenants) is the primary tenant↔target link and
        // powers tenant-filtered target pickers; the extended-tag-set TAGS
        // seeded below are auxiliary metadata so the tags UI has demo data
        // covering all three set types (docs/extended-tag-sets-plan.md).
        (string TenantSlug, string Tag, string[] Targets)[] tagSpecs =
        [
            ("grad-dubrovnik", "DBK", ["demo-web-prod-01", "demo-web-stage-01"]),
            ("grad-split", "ST", ["demo-web-prod-02"]),
            ("ministarstvo-financija", "MFIN", ["demo-api-prod-03", "demo-dmz-gw-01"]),
        ];

        var tagSvc = new TagService(dbFactory);
        await using (var tdb = await dbFactory.CreateDbContextAsync())
        {
            var targetIds = await tdb.DeploymentTargets
                .Where(t => t.Name.StartsWith("demo-"))
                .ToDictionaryAsync(t => t.Name, t => t.Id);
            var tenantIds = await tdb.Tenants.ToDictionaryAsync(t => t.Slug, t => t.Id);

            // ── "Hosting" — MultiSelect, scoped Tenant + Target ──────────────
            var allSets = await tagSvc.GetAllSetsAsync();
            var hosting = allSets.FirstOrDefault(s => s.Name == "Hosting")
                ?? await tagSvc.CreateSetAsync(
                    "Hosting", "Demo hosting tag set",
                    KrakenDeploy.Server.Core.Domain.Tags.TagSetType.MultiSelect,
                    [
                        KrakenDeploy.Server.Core.Domain.Tags.TaggableEntityKind.Tenant,
                        KrakenDeploy.Server.Core.Domain.Tags.TaggableEntityKind.DeploymentTarget,
                    ],
                    sortOrder: 0);
            // Reload with tags for idempotent tag lookups.
            hosting = await tagSvc.GetSetAsync(hosting.Id) ?? hosting;

            foreach (var (tenantSlug, tagName, targetNames) in tagSpecs)
            {
                if (!tenantIds.TryGetValue(tenantSlug, out var tenantId))
                {
                    continue;
                }

                var tag = hosting.Tags.FirstOrDefault(t => t.Name == tagName)
                          ?? await tagSvc.CreateTagAsync(hosting.Id, tagName, null, null);

                // Tag the tenant itself + its targets (replace-per-set is idempotent).
                await tagSvc.SetAppliedTagsAsync(
                    hosting.Id, KrakenDeploy.Server.Core.Domain.Tags.TaggableEntityKind.Tenant,
                    tenantId, [tag.Id]);
                foreach (var tn in targetNames)
                {
                    if (targetIds.TryGetValue(tn, out var tid))
                    {
                        await tagSvc.SetAppliedTagsAsync(
                            hosting.Id,
                            KrakenDeploy.Server.Core.Domain.Tags.TaggableEntityKind.DeploymentTarget,
                            tid, [tag.Id]);
                    }
                }

                // Direct association (the primary link) for the same pairs.
                foreach (var tn in targetNames)
                {
                    if (!targetIds.TryGetValue(tn, out var tid))
                    {
                        continue;
                    }
                    await using var adb = await dbFactory.CreateDbContextAsync();
                    var target = await adb.DeploymentTargets
                        .Include(t => t.Tenants)
                        .FirstAsync(t => t.Id == tid);
                    if (target.Tenants.All(t => t.Id != tenantId))
                    {
                        var tenant = await adb.Tenants.FirstAsync(t => t.Id == tenantId);
                        target.Tenants.Add(tenant);
                        await adb.SaveChangesAsync();
                    }
                }
            }

            // ── "Region" — FreeText, scoped Tenant ───────────────────────────
            var region = allSets.FirstOrDefault(s => s.Name == "Region")
                ?? await tagSvc.CreateSetAsync(
                    "Region", "Demo free-text region identifier",
                    KrakenDeploy.Server.Core.Domain.Tags.TagSetType.FreeText,
                    [KrakenDeploy.Server.Core.Domain.Tags.TaggableEntityKind.Tenant],
                    sortOrder: 1);
            if (tenantIds.TryGetValue("grad-dubrovnik", out var dbkId))
            {
                await tagSvc.SetFreeTextValueAsync(
                    region.Id, KrakenDeploy.Server.Core.Domain.Tags.TaggableEntityKind.Tenant,
                    dbkId, "HR-South");
            }

            // ── "Tier" — SingleSelect, scoped Environment ────────────────────
            var tier = allSets.FirstOrDefault(s => s.Name == "Tier")
                ?? await tagSvc.CreateSetAsync(
                    "Tier", "Demo environment criticality tier",
                    KrakenDeploy.Server.Core.Domain.Tags.TagSetType.SingleSelect,
                    [KrakenDeploy.Server.Core.Domain.Tags.TaggableEntityKind.Environment],
                    sortOrder: 2);
            tier = await tagSvc.GetSetAsync(tier.Id) ?? tier;
            var critical = tier.Tags.FirstOrDefault(t => t.Name == "Critical")
                           ?? await tagSvc.CreateTagAsync(tier.Id, "Critical", "#e63946", null);
            var prodEnvId = await tdb.Environments
                .Where(e => e.Name == "Production")
                .Select(e => (Guid?)e.Id)
                .FirstOrDefaultAsync();
            if (prodEnvId is { } peid)
            {
                await tagSvc.SetAppliedTagsAsync(
                    tier.Id, KrakenDeploy.Server.Core.Domain.Tags.TaggableEntityKind.Environment,
                    peid, [critical.Id]);
            }
        }

        // Tag untagged deployments of every tenant-connected project,
        // round-robin over that project's tenants, so the per-tenant matrix
        // renders cells wherever tenants are wired up.
        await using var db2 = await dbFactory.CreateDbContextAsync();
        var tenantsByProject = (await db2.Tenants
                .Include(t => t.Projects)
                .ToListAsync())
            .SelectMany(t => t.Projects.Select(p => (ProjectId: p.Id, TenantId: t.Id)))
            .GroupBy(x => x.ProjectId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.TenantId).ToList());

        // Only OUR deployments: without the marker filter this would stamp a
        // demo TenantId onto real, operator-created deployments of
        // tenant-connected projects (e.g. the hand-made "argosy" project).
        var untagged = await db2.Deployments
            .Where(d => d.TenantId == null && d.CauseDetail == SeedMarker)
            .Select(d => new { Deployment = d, d.Release.ProjectId })
            .OrderBy(x => x.Deployment.CreatedUtc)
            .ToListAsync();

        var i = 0;
        foreach (var entry in untagged)
        {
            if (tenantsByProject.TryGetValue(entry.ProjectId, out var ids))
            {
                entry.Deployment.TenantId = ids[i++ % ids.Count];
            }
        }
        await db2.SaveChangesAsync();
    }

    /// <summary>
    /// Demo channels: "hotfix" on argosy-web and "lts" on billing-service,
    /// with each project's newest release moved onto the channel. Those
    /// releases already have deployments (ladder seeding), so the channel
    /// pill shows up in the Projects matrix immediately. Idempotent.
    /// </summary>
    private static async Task SeedChannelsAsync(
        IDbContextFactory<KrakenDbContext> dbFactory, ChannelService channelSvc)
    {
        (string ProjectSlug, string Channel)[] specs =
            [("argosy-web", "hotfix"), ("billing-service", "lts")];

        foreach (var (slug, channelName) in specs)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Slug == slug);
            if (project is null)
            {
                continue;
            }

            var channel = await db.Channels.AsNoTracking()
                .FirstOrDefaultAsync(c => c.ProjectId == project.Id && c.Name == channelName)
                ?? await channelSvc.CreateAsync(project.Id, channelName, isDefault: false,
                    lifecycleId: null, versionRange: null, versionTag: null);

            // Newest release that actually has a deployment → visible pill.
            var release = await db.Releases
                .Where(r => r.ProjectId == project.Id && r.ChannelId == null
                         && db.Deployments.Any(d => d.ReleaseId == r.Id))
                .OrderByDescending(r => r.CreatedUtc)
                .FirstOrDefaultAsync();
            if (release is not null)
            {
                db.Releases.Attach(release);
                release.ChannelId = channel.Id;
                await db.SaveChangesAsync();
            }
        }
    }

    /// <summary>
    private static TaskLogLiveEntry Log(Deployment d, int seq, string msg, string level, DateTimeOffset ts) => new()
    {
        TaskId = d.Id,
        StepIndex = -1,
        TargetId = null,
        Sequence = seq,
        Message = msg,
        Level = level,
        Timestamp = ts,
    };

    private static TaskStepOutcome Outcome(Guid deploymentId, int idx, string name, StepOutcomeKind kind,
        DateTimeOffset started, DateTimeOffset completed, string? error = null) => new()
    {
        TaskId = deploymentId,
        StepIndex = idx,
        StepName = name,
        Outcome = kind,
        Required = true,
        IsServerSide = false,
        AttemptCount = 1,
        StartedUtc = started,
        CompletedUtc = completed,
        ErrorMessage = error,
    };

    /// <summary>
    /// Deletes ONLY demo-seeded rows. A database this tool ran against can
    /// also hold real data (the matrix seeder even adds rows to non-demo
    /// projects), so nothing here may delete a whole table: demo projects are
    /// matched by <see cref="DemoProjectSpecs"/> slug PLUS the seeder's own
    /// description stamp, and rows the seeder creates on OTHER projects by
    /// their <see cref="SeedMarker"/>. Anything real history has since built
    /// on is ADOPTED rather than deleted: a marker release an operator has
    /// deployed, a demo target a surviving task still references (both are
    /// RESTRICT FKs that would otherwise abort the whole clear), process
    /// steps scaffolded onto a non-demo project, and tenant tags stamped
    /// onto real deployments all stay.
    /// Known limitations: ladder releases seeded BEFORE the marker existed
    /// carry NULL notes and are not recognised on non-demo projects; a real
    /// release whose operator-typed notes are exactly the marker text is
    /// treated as seeded (deleted if it was never deployed).
    /// </summary>
    private static async Task ClearAsync(IDbContextFactory<KrakenDbContext> dbFactory)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        // Two flushes (the adoption queries below must see the task deletes'
        // cascades); one transaction keeps the clear all-or-nothing.
        await using var tx = await db.Database.BeginTransactionAsync();

        // Slug alone is not identity — an operator can own a project with a
        // colliding slug. Require the description the seeder stamps at
        // creation, and prefer leftovers over deleting a real project.
        var demoSlugs = DemoProjectSpecs.Select(s => s.Slug).ToArray();
        var demoProjectIds = await db.Projects
            .Where(p => demoSlugs.Contains(p.Slug)
                        && p.Description != null
                        && p.Description.EndsWith(DemoDescriptionSuffix))
            .Select(p => p.Id)
            .ToListAsync();

        // Tasks first — they hold RESTRICT FKs to releases and targets.
        // Marker-only matching would leave behind hand-made deployments of a
        // demo project, and those would then block the release/project
        // deletes below, so match either. d.ProjectId is checked besides
        // d.Release.ProjectId because hand-made rows stamp it while
        // matrix-seeded rows leave it default.
        db.Deployments.RemoveRange(await db.Deployments
            .Where(d => d.CauseDetail == SeedMarker
                        || demoProjectIds.Contains(d.ProjectId)
                        || demoProjectIds.Contains(d.Release.ProjectId))
            .ToListAsync());

        // Runbook runs first: runbook_runs -> runbooks is ON DELETE RESTRICT
        // (run history never cascades), so leaving them aborts the whole clear.
        var demoRunbooks = await db.Runbooks
            .Where(r => demoProjectIds.Contains(r.ProjectId))
            .ToListAsync();
        var demoRunbookIds = demoRunbooks.Select(r => r.Id).ToList();
        db.RunbookRuns.RemoveRange(await db.RunbookRuns
            .Where(r => demoRunbookIds.Contains(r.RunbookId))
            .ToListAsync());
        db.Runbooks.RemoveRange(demoRunbooks);

        // Processes are polymorphic (no FK to their owner) — the owning service
        // normally deletes them, so the demo projects' and demo runbooks' rows
        // would otherwise be orphaned by the deletes below.
        db.Processes.RemoveRange(await db.Processes
            .Where(p => (p.OwnerKind == ProcessOwnerKind.Project && demoProjectIds.Contains(p.OwnerId))
                     || (p.OwnerKind == ProcessOwnerKind.Runbook && demoRunbookIds.Contains(p.OwnerId)))
            .ToListAsync());

        // Flush so the adoption queries below see which tasks (and their
        // cascaded assignment/outcome rows) are actually gone.
        await db.SaveChangesAsync();

        // Demo projects' releases, plus marker-stamped ladder rungs the matrix
        // seeder created on non-demo projects. A marker release an operator
        // has DEPLOYED is adopted: server_tasks.release_id is RESTRICT, so
        // deleting it would abort the whole clear — and the deployment is
        // real history. (A demo project's releases can't be adopted: all of
        // that project's deployments were just deleted.)
        var adoptedReleases = await db.Releases
            .CountAsync(r => r.ReleaseNotes == SeedMarker
                             && db.Deployments.Any(d => d.ReleaseId == r.Id));
        db.Releases.RemoveRange(await db.Releases
            .Where(r => (demoProjectIds.Contains(r.ProjectId) || r.ReleaseNotes == SeedMarker)
                        && !db.Deployments.Any(d => d.ReleaseId == r.Id))
            .ToListAsync());

        db.Projects.RemoveRange(await db.Projects
            .Where(p => demoProjectIds.Contains(p.Id))
            .ToListAsync());

        // Project variable sets cascade with their project; the shared demo
        // library set is project-independent and needs explicit removal.
        db.VariableSets.RemoveRange(await db.VariableSets
            .Where(s => s.Name == DemoLibrarySetName).ToListAsync());
        db.Tenants.RemoveRange(await db.Tenants.Where(t => DemoTenantSlugs.Contains(t.Slug)).ToListAsync());

        // Demo targets: adopt any target a SURVIVING task still references.
        // Both task_target_assignments and task_step_outcomes carry RESTRICT
        // FKs to targets, and deleting the reference rows instead would
        // falsify the surviving task's recorded history.
        var demoTargets = await db.DeploymentTargets
            .Where(t => t.Name.StartsWith("demo-")).ToListAsync();
        var demoTargetIds = demoTargets.Select(t => t.Id).ToList();
        var referencedTargetIds = (await db.TaskTargetAssignments
                .Where(a => demoTargetIds.Contains(a.TargetId))
                .Select(a => a.TargetId).Distinct().ToListAsync())
            .Concat(await db.TaskStepOutcomes
                .Where(o => o.TargetId != null && demoTargetIds.Contains(o.TargetId.Value))
                .Select(o => o.TargetId!.Value).Distinct().ToListAsync())
            .ToHashSet();
        db.DeploymentTargets.RemoveRange(demoTargets.Where(t => !referencedTargetIds.Contains(t.Id)));

        await db.SaveChangesAsync();
        await tx.CommitAsync();

        if (adoptedReleases > 0)
        {
            Console.WriteLine(
                $"Kept {adoptedReleases} seed-marked release(s) that real deployments reference.");
        }
        if (referencedTargetIds.Count > 0)
        {
            Console.WriteLine(
                $"Kept {referencedTargetIds.Count} demo target(s) that surviving task history references.");
        }
    }

    private const string DemoLibrarySetName = "Company Defaults";

    /// <summary>
    /// One demo variable definition. Environment/tenant scope is carried as a
    /// NAME and resolved at insert time — a lookup miss skips the spec with a
    /// warning instead of silently collapsing it to an unscoped duplicate
    /// (an all-null <see cref="VariableScope"/> matches every context).
    /// </summary>
    private sealed record DemoVarSpec(
        string Project, string Name, string Value, VariableType Type,
        string? EnvName = null, string? TenantSlug = null,
        string[]? Roles = null, Guid? ChannelId = null);

    /// <summary>
    /// Variables for the demo projects: unscoped + environment-scoped +
    /// role-scoped + channel-scoped + sensitive project variables, a shared
    /// library set included by two projects, and tenant-scoped values for the
    /// connected demo tenants. Idempotent on (name, env, tenant, channel), so
    /// a partially-seeded project converges on re-run.
    /// </summary>
    private static async Task SeedVariablesAsync(
        IDbContextFactory<KrakenDbContext> dbFactory, VariableService variableSvc)
    {
        var system = KrakenDeploy.Server.Core.Domain.Security.CallerAuthorization.System;

        await using var db = await dbFactory.CreateDbContextAsync();
        var projectIds = await db.Projects.ToDictionaryAsync(p => p.Slug, p => p.Id);
        var envIds = await db.Environments.ToDictionaryAsync(e => e.Name, e => e.Id);
        var tenantIds = await db.Tenants.Where(t => DemoTenantSlugs.Contains(t.Slug))
            .ToDictionaryAsync(t => t.Slug, t => t.Id);

        // Scope-aware idempotency key: name-only skipping would freeze a
        // half-seeded name group (e.g. only the Dev-scoped variant) forever.
        static string Key(string name, Guid? envId, Guid? tenantId, Guid? channelId) =>
            $"{name.ToLowerInvariant()}|{envId}|{tenantId}|{channelId}";

        // Resolves a spec's scope; returns false (with a console warning) when a
        // named environment/tenant doesn't exist in this database.
        bool TryResolveScope(DemoVarSpec spec, out VariableScope? scope,
            out Guid? envId, out Guid? tenantId)
        {
            scope = null;
            envId = null;
            tenantId = null;
            if (spec.EnvName is not null)
            {
                if (!envIds.TryGetValue(spec.EnvName, out var e))
                {
                    Console.WriteLine(
                        $"WARNING: skipping demo variable '{spec.Name}' — no environment named " +
                        $"'{spec.EnvName}' exists (environments are only auto-created into an empty database).");
                    return false;
                }
                envId = e;
            }
            if (spec.TenantSlug is not null)
            {
                if (!tenantIds.TryGetValue(spec.TenantSlug, out var t))
                {
                    Console.WriteLine(
                        $"WARNING: skipping demo variable '{spec.Name}' — demo tenant '{spec.TenantSlug}' not found.");
                    return false;
                }
                tenantId = t;
            }
            if (envId is not null || tenantId is not null || spec.Roles is not null || spec.ChannelId is not null)
            {
                scope = new VariableScope
                {
                    EnvironmentId = envId,
                    TenantId      = tenantId,
                    Roles         = spec.Roles is { } roles ? [.. roles] : null,
                    ChannelId     = spec.ChannelId,
                };
            }
            return true;
        }

        var specs = new List<DemoVarSpec>();

        if (projectIds.TryGetValue("argosy-web", out var argosyWebId))
        {
            var hotfixChannelId = await db.Channels
                .Where(c => c.ProjectId == argosyWebId && c.Name == "hotfix")
                .Select(c => (Guid?)c.Id).FirstOrDefaultAsync();

            specs.AddRange(
            [
                new("argosy-web", "Database.Name", "argosy_web", VariableType.Text),
                new("argosy-web", "Database.Password", "demo-P@ssw0rd-web", VariableType.Sensitive),
                new("argosy-web", "Api.BaseUrl", "https://dev.argosy.example/api", VariableType.Text, EnvName: "Development"),
                new("argosy-web", "Api.BaseUrl", "https://argosy.example/api", VariableType.Text, EnvName: "Production"),
                new("argosy-web", "Log.Level", "Warning", VariableType.Text),
                new("argosy-web", "Log.Level", "Debug", VariableType.Text, EnvName: "Development"),
                new("argosy-web", "IIS.AppPool", "argosy-web-pool", VariableType.Text, Roles: ["web-server"]),
                new("argosy-web", "Tenant.DisplayName", "Grad Dubrovnik", VariableType.Text, TenantSlug: "grad-dubrovnik"),
                new("argosy-web", "Tenant.DisplayName", "Grad Split", VariableType.Text, TenantSlug: "grad-split"),
                new("argosy-web", "Tenant.DbSchema", "dbk", VariableType.Text, TenantSlug: "grad-dubrovnik"),
                new("argosy-web", "Tenant.DbSchema", "st", VariableType.Text, TenantSlug: "grad-split"),
            ]);
            if (hotfixChannelId is not null)
            {
                specs.Add(new("argosy-web", "Deploy.Ring", "hotfix", VariableType.Text, ChannelId: hotfixChannelId));
            }
        }

        if (projectIds.ContainsKey("argosy-api"))
        {
            specs.AddRange(
            [
                new("argosy-api", "Api.TimeoutSeconds", "30", VariableType.Text),
                new("argosy-api", "Database.Password", "demo-P@ssw0rd-api", VariableType.Sensitive),
                new("argosy-api", "Tenant.ApiKey", "demo-key-dbk", VariableType.Sensitive, TenantSlug: "grad-dubrovnik"),
                new("argosy-api", "Tenant.ApiKey", "demo-key-st", VariableType.Sensitive, TenantSlug: "grad-split"),
            ]);
        }

        if (projectIds.ContainsKey("billing-service"))
        {
            specs.AddRange(
            [
                new("billing-service", "Billing.Currency", "EUR", VariableType.Text),
                new("billing-service", "Tenant.InvoicePrefix", "MF", VariableType.Text, TenantSlug: "ministarstvo-financija"),
            ]);
        }

        foreach (var group in specs.GroupBy(s => s.Project))
        {
            var projectId = projectIds[group.Key];
            var existing = (await variableSvc.GetVariablesAsync(projectId).ConfigureAwait(false))
                .Select(v => Key(v.Name, v.Scope.EnvironmentId, v.Scope.TenantId, v.Scope.ChannelId))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var spec in group)
            {
                if (!TryResolveScope(spec, out var scope, out var envId, out var tenantId))
                {
                    continue;
                }
                if (existing.Contains(Key(spec.Name, envId, tenantId, spec.ChannelId)))
                {
                    continue;
                }
                await variableSvc.CreateVariableAsync(projectId, spec.Name, spec.Value, spec.Type, scope, system)
                    .ConfigureAwait(false);
            }
        }

        // Shared library set included by both Argosy projects — gives the All
        // Variables tab a non-project source and the preview a lower-precedence
        // layer to override.
        var librarySet = (await variableSvc.GetLibrarySetsAsync().ConfigureAwait(false))
            .FirstOrDefault(s => s.Name == DemoLibrarySetName)
            ?? await variableSvc.CreateLibrarySetAsync(DemoLibrarySetName,
                "Shared demo defaults (SMTP, branding)").ConfigureAwait(false);

        var setExisting = (await variableSvc.GetVariablesInSetAsync(librarySet.Id).ConfigureAwait(false))
            .Select(v => Key(v.Name, v.Scope.EnvironmentId, v.Scope.TenantId, v.Scope.ChannelId))
            .ToHashSet(StringComparer.Ordinal);
        DemoVarSpec[] setSpecs =
        [
            new("", "Company.Name", "Kraken Demo d.o.o.", VariableType.Text),
            new("", "Smtp.Host", "smtp.demo.local", VariableType.Text),
            new("", "Smtp.Host", "smtp.prod.demo.local", VariableType.Text, EnvName: "Production"),
            // Same name as the project-level variable: the project definition
            // wins on an origin tiebreak — visible in the Preview tab.
            new("", "Log.Level", "Information", VariableType.Text),
        ];
        foreach (var spec in setSpecs)
        {
            if (!TryResolveScope(spec, out var scope, out var envId, out var tenantId))
            {
                continue;
            }
            if (setExisting.Contains(Key(spec.Name, envId, tenantId, spec.ChannelId)))
            {
                continue;
            }
            await variableSvc.CreateVariableInSetAsync(librarySet.Id, spec.Name, spec.Value, spec.Type, scope, system)
                .ConfigureAwait(false);
        }

        foreach (var slug in new[] { "argosy-web", "argosy-api" })
        {
            if (projectIds.TryGetValue(slug, out var pid))
            {
                // IncludeSetAsync is a no-op when already included.
                await variableSvc.IncludeSetAsync(pid, librarySet.Id, system).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A few runbooks per demo project so the project Runbooks tab lists real
    /// rows. Idempotent: skips names that already exist.
    /// </summary>
    private static async Task SeedRunbooksAsync(
        IDbContextFactory<KrakenDbContext> dbFactory, RunbookService runbookSvc)
    {
        var system = KrakenDeploy.Server.Core.Domain.Security.CallerAuthorization.System;

        (string ProjectSlug, string Name, string Description)[] specs =
        [
            ("argosy-web", "Restart IIS app pool", "Recycle the argosy-web application pool on all web servers."),
            ("argosy-web", "Rotate log files", "Archive and truncate IIS + app logs older than 14 days."),
            ("argosy-api", "Restart API service", "Restart the argosy-api Windows service."),
            ("billing-service", "Backup database", "Full backup of the billing database to the backup share."),
            ("billing-service", "Reindex database", "Rebuild fragmented indexes outside business hours."),
        ];

        await using var db = await dbFactory.CreateDbContextAsync();
        var projectIds = await db.Projects.ToDictionaryAsync(p => p.Slug, p => p.Id);

        foreach (var group in specs.GroupBy(s => s.ProjectSlug))
        {
            if (!projectIds.TryGetValue(group.Key, out var projectId))
            {
                continue;
            }
            var existing = (await runbookSvc.GetAllAsync(projectId).ConfigureAwait(false))
                .Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, name, description) in group)
            {
                if (existing.Contains(name))
                {
                    continue;
                }
                await runbookSvc.CreateAsync(projectId, name, description, system).ConfigureAwait(false);
            }
        }
    }
}
