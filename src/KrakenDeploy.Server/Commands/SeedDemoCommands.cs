using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Deployments;
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
    public static async Task<int> RunAsync(string[] args, string contentRoot)
    {
        var clear = args.Contains("--clear");

        var builder = CliHost.CreateBuilder(contentRoot);

        // seed-demo writes fake demo data (Grad Dubrovnik, Argosy Web, demo targets, …)
        // into the Default Space. It is a dev-only single-instance tool; in multi-account
        // mode (production SaaS) resolving a real tenant and filling it with demo garbage
        // is a footgun, so refuse outright rather than offer --account.
        if (builder.Configuration.GetValue("MultiAccount:Enabled", false))
        {
            Console.Error.WriteLine(
                "seed-demo is a dev-only single-instance tool and is not supported in " +
                "multi-account mode (it would write demo data into a real tenant database).");
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
            Console.WriteLine("Demo data cleared (deployments, releases, projects, demo tenants, demo targets).");
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
        var specs = new[]
        {
            ("Argosy Web", "argosy-web", "2026.6.4"),
            ("Argosy API", "argosy-api", "2026.6.4"),
            ("Billing Service", "billing-service", "4.18.0"),
            ("Identity Provider", "identity-provider", "3.2.1"),
        };

        var releases = new List<(Guid ReleaseId, string Project, string Version)>();
        foreach (var (name, slug, version) in specs)
        {
            await using var pdb = await dbFactory.CreateDbContextAsync();
            var project = await pdb.Projects.FirstOrDefaultAsync(p => p.Slug == slug);
            if (project is null)
            {
                project = await projectSvc.CreateAsync(name, slug, $"{name} — demo project").ConfigureAwait(false);
                await processSvc.AddStepAsync(project.Id, "Deploy package to IIS", "Script", "", ["web-server"], [], KrakenDeploy.Server.Core.Domain.Security.CallerAuthorization.System).ConfigureAwait(false);
                await processSvc.AddStepAsync(project.Id, "Run database migrations", "Script", "", ["db"], [], KrakenDeploy.Server.Core.Domain.Security.CallerAuthorization.System).ConfigureAwait(false);
                await processSvc.AddStepAsync(project.Id, "Smoke test — health endpoint", "Script", "", ["web-server"], [], KrakenDeploy.Server.Core.Domain.Security.CallerAuthorization.System).ConfigureAwait(false);
            }

            await using var rdb = await dbFactory.CreateDbContextAsync();
            var release = await rdb.Releases.FirstOrDefaultAsync(r => r.ProjectId == project.Id && r.Version == version)
                          ?? await releaseSvc.CreateAsync(project.Id, version, KrakenDeploy.Server.Core.Domain.Security.CallerAuthorization.System).ConfigureAwait(false);
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

        Console.WriteLine($"Seeded: {targets.Count} targets, {specs.Length} projects/releases, demo deployments, 3 tenants.");
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
        if (await db.Deployments.AnyAsync())
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
                NextLogSequence = 0,
                // Provenance (fix 6): seed rows are created directly (not via the
                // service), so stamp the columns inline. Demo data = CLI seed.
                Cause = ServerTaskCause.Cli,
                CreatedByDisplay = "System (seed-demo)",
                CauseDetail = "seed-demo",
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
        running.NextLogSequence = 7;

        var failed = Make("Argosy API", prod, DeploymentStatus.Failed, 70, 90, tgt?.Id);
        demoLogs.AddRange(
            Log(failed, 0, "═══ Step 1/3 · Deploy package to IIS ═══", "info", failed.StartedUtc!.Value),
            Log(failed, 1, "api-prod-03: deploying ArgosyAPI 2026.6.4", "info", failed.StartedUtc!.Value),
            Log(failed, 2, "Smoke test — health endpoint timed out after 90s", "error", failed.StartedUtc!.Value.AddSeconds(90)));
        failed.NextLogSequence = 3;
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

            foreach (var version in LadderVersions(existingVersions))
            {
                await releaseSvc.CreateAsync(project.Id, version, KrakenDeploy.Server.Core.Domain.Security.CallerAuthorization.System).ConfigureAwait(false);
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
                        EnvironmentId = envs[e],
                        Targets = targetId is { } t
                            ? [new TaskTargetAssignment { TargetId = t, AddedUtc = now }]
                            : [],
                        Status = status,
                        StartedUtc = started,
                        CompletedUtc = started.AddMinutes(3),
                        NextLogSequence = 0,
                        Cause = ServerTaskCause.Cli,
                        CreatedByDisplay = "System (seed-demo)",
                        CauseDetail = "seed-demo",
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

        var untagged = await db2.Deployments
            .Where(d => d.TenantId == null)
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

    private static async Task ClearAsync(IDbContextFactory<KrakenDbContext> dbFactory)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.Deployments.RemoveRange(await db.Deployments.ToListAsync());
        db.Releases.RemoveRange(await db.Releases.ToListAsync());
        db.Projects.RemoveRange(await db.Projects.ToListAsync());
        db.Tenants.RemoveRange(await db.Tenants.Where(t => DemoTenantSlugs.Contains(t.Slug)).ToListAsync());
        db.DeploymentTargets.RemoveRange(await db.DeploymentTargets.Where(t => t.Name.StartsWith("demo-")).ToListAsync());
        await db.SaveChangesAsync();
    }
}
