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
        var connectionString = builder.Configuration.GetConnectionString("KrakenDb")
            ?? throw new InvalidOperationException("Connection string 'KrakenDb' is not configured.");

        builder.Services.AddKrakenDeployData(connectionString);
        builder.Services.AddSingleton<IEncryptionService>(
            _ => new AesEncryptionService(
                Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))));

        using var host = builder.Build();
        await using var scope = host.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var db = sp.GetRequiredService<KrakenDbContext>();
        await db.Database.MigrateAsync().ConfigureAwait(false);
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
                await processSvc.AddStepAsync(project.Id, "Deploy package to IIS", "Script", "", ["web-server"], []).ConfigureAwait(false);
                await processSvc.AddStepAsync(project.Id, "Run database migrations", "Script", "", ["db"], []).ConfigureAwait(false);
                await processSvc.AddStepAsync(project.Id, "Smoke test — health endpoint", "Script", "", ["web-server"], []).ConfigureAwait(false);
            }

            await using var rdb = await dbFactory.CreateDbContextAsync();
            var release = await rdb.Releases.FirstOrDefaultAsync(r => r.ProjectId == project.Id && r.Version == version)
                          ?? await releaseSvc.CreateAsync(project.Id, version).ConfigureAwait(false);
            releases.Add((release.Id, name, version));
        }

        // ── Deployments (direct insert; varied terminal + live states) ────
        await SeedDeploymentsAsync(dbFactory, releases, envs.Select(e => e.Id).ToList(), targets);

        // ── Tenants + project connections + deployment tagging ────────────
        await SeedTenantsAsync(dbFactory, sp.GetRequiredService<TenantService>()).ConfigureAwait(false);

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

        Deployment Make(string project, Guid env, DeploymentStatus status, int startedMinAgo, int? durationSec, Guid? targetId)
        {
            var started = now.AddMinutes(-startedMinAgo);
            return new Deployment
            {
                SpaceId = WellKnown.DefaultSpaceId,
                ReleaseId = rel[project],
                EnvironmentId = env,
                TargetId = targetId,
                Status = status,
                StartedUtc = status == DeploymentStatus.Queued ? null : started,
                CompletedUtc = durationSec is { } d ? started.AddSeconds(d) : null,
                NextLogSequence = 0,
            };
        }

        var running = Make("Argosy Web", prod, DeploymentStatus.Running, 4, null, tgt?.Id);
        running.LogEntries =
        [
            Log(running, 0, "═══ Step 1/3 · Deploy package to IIS ═══", "info", now.AddMinutes(-4)),
            Log(running, 1, "$ kraken deploy-package ArgosyWeb 2026.6.4", "info", now.AddMinutes(-4)),
            Log(running, 2, "Acquiring package ArgosyWeb 2026.6.4 (delta: 4.2 MB of 88 MB)", "info", now.AddMinutes(-3)),
            Log(running, 3, "web-prod-01: site re-pointed, app pool recycled", "ok", now.AddMinutes(-3)),
            Log(running, 4, "web-prod-02: agent on 1.4.0 (server is 1.4.2) — consider upgrading", "warning", now.AddMinutes(-2)),
            Log(running, 5, "═══ Step 2/3 · Run database migrations ═══", "info", now.AddMinutes(-2)),
            Log(running, 6, "Applying 2 pending migrations to DB-PROD-01", "info", now.AddMinutes(-1)),
        ];
        running.NextLogSequence = 7;

        var failed = Make("Argosy API", prod, DeploymentStatus.Failed, 70, 90, tgt?.Id);
        failed.LogEntries =
        [
            Log(failed, 0, "═══ Step 1/3 · Deploy package to IIS ═══", "info", failed.StartedUtc!.Value),
            Log(failed, 1, "api-prod-03: deploying ArgosyAPI 2026.6.4", "info", failed.StartedUtc!.Value),
            Log(failed, 2, "Smoke test — health endpoint timed out after 90s", "error", failed.StartedUtc!.Value.AddSeconds(90)),
        ];
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

        // Step outcomes for the failed deployment (no nav collection on Deployment).
        db.DeploymentStepOutcomes.AddRange(
            Outcome(failed.Id, 0, "Deploy package to IIS", StepOutcomeKind.Succeeded, failedStart, failedStart.AddSeconds(40)),
            Outcome(failed.Id, 1, "Run database migrations", StepOutcomeKind.Succeeded, failedStart.AddSeconds(40), failedStart.AddSeconds(55)),
            Outcome(failed.Id, 2, "Smoke test — health endpoint", StepOutcomeKind.TimedOut, failedStart.AddSeconds(55), failedStart.AddSeconds(90),
                "Health check did not return 200 within 90s. Last response: 503 Service Unavailable."));
        await db.SaveChangesAsync();
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
            ("Grad Dubrovnik", "grad-dubrovnik", ["argosy-web", "argosy-api"]),
            ("Grad Split", "grad-split", ["argosy-web", "argosy-api"]),
            ("Ministarstvo financija", "ministarstvo-financija", ["billing-service"]),
        ];

        await using var db = await dbFactory.CreateDbContextAsync();
        var projectIds = await db.Projects.ToDictionaryAsync(p => p.Slug, p => p.Id);

        var tenantIds = new Dictionary<string, Guid>();
        foreach (var (name, slug, projectSlugs) in specs)
        {
            var tenant = await tenantSvc.GetBySlugAsync(slug).ConfigureAwait(false)
                         ?? await tenantSvc.CreateAsync(name, slug, $"{name} — demo tenant").ConfigureAwait(false);
            tenantIds[slug] = tenant.Id;

            foreach (var ps in projectSlugs)
            {
                if (projectIds.TryGetValue(ps, out var pid))
                {
                    // ConnectProjectAsync is a no-op when already connected.
                    await tenantSvc.ConnectProjectAsync(tenant.Id, pid).ConfigureAwait(false);
                }
            }
        }

        // Tag the multi-tenant projects' untagged deployments alternating
        // between the two city tenants.
        var cityTenants = new[] { tenantIds["grad-dubrovnik"], tenantIds["grad-split"] };
        string[] multiTenantSlugs = ["argosy-web", "argosy-api"];

        await using var db2 = await dbFactory.CreateDbContextAsync();
        var untagged = await db2.Deployments
            .Where(d => d.TenantId == null && multiTenantSlugs.Contains(d.Release.Project.Slug))
            .OrderBy(d => d.CreatedUtc)
            .ToListAsync();

        for (var i = 0; i < untagged.Count; i++)
        {
            untagged[i].TenantId = cityTenants[i % cityTenants.Length];
        }
        await db2.SaveChangesAsync();
    }

    private static DeploymentLogEntry Log(Deployment d, int seq, string msg, string level, DateTimeOffset ts) => new()
    {
        Sequence = seq,
        Message = msg,
        Level = level,
        Timestamp = ts,
    };

    private static DeploymentStepOutcome Outcome(Guid deploymentId, int idx, string name, StepOutcomeKind kind,
        DateTimeOffset started, DateTimeOffset completed, string? error = null) => new()
    {
        DeploymentId = deploymentId,
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
