using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;
using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Runbooks;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Spaces;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Core.Domain.Tenants;
using KrakenDeploy.Server.Core.Domain.Variables;
using KrakenDeploy.Server.Data.Encryption;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Transport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests.OrchestratorHarness;

/// <summary>
/// End-to-end test harness for <see cref="DeploymentWorker"/> that:
/// <list type="bullet">
///   <item>Spins up the real DI container (via <c>AddKrakenDeployData</c> +
///         a handful of transport-layer overrides) backed by the shared
///         <see cref="PostgresFixture"/>.</item>
///   <item>Substitutes <see cref="FakeAgentHubContext"/> for the real SignalR
///         hub context so target-side wave dispatches resolve synchronously
///         via configured per-target <see cref="FakeAgent"/> scripts —
///         deterministic, no Task.Delay games, no real agent runtime.</item>
///   <item>Exposes a small fluent surface to seed Projects / Environments /
///         Releases / Targets and to drive
///         <see cref="DeploymentWorker.DispatchForTestAsync"/> to completion.</item>
/// </list>
///
/// <para>
/// <strong>Why this harness exists</strong>: the M14.2 plan body called out
/// an "no E2E orchestrator harness" gap, and M-RollingDeployments Phases 1b /
/// 2 / 3 added ~500 lines of new orchestrator logic with zero coverage on
/// top of that. This harness closes the loop — the new behaviours
/// (per-target fan-out, rolling-window batching, per-target drop-out) are
/// observable end-to-end through the shared
/// <see cref="DeploymentStepOutcome"/> + <see cref="Deployment.Status"/>
/// state without instantiating a real agent process.
/// </para>
///
/// <para>
/// <strong>Scope</strong>: target-side dispatch + per-target outcomes. The
/// harness wires <see cref="ServerScriptStepRunner"/> + <see cref="DeployReleaseStepRunner"/>
/// for compile-completeness but the seeded test plans use only target-side
/// steps so the server runners aren't exercised (their dependencies
/// — UiHub, etc. — are wired up but inert).
/// </para>
/// </summary>
public sealed class OrchestratorTestHarness : IAsyncDisposable
{
    private readonly PostgresFixture _postgres;
    private readonly ServiceProvider _services;
    private readonly InMemoryAgentConnectionRegistry _connectionRegistry = new();
    private readonly PendingSubPlanRegistry _subPlans = new();
    private readonly ConcurrentDictionary<Guid, FakeAgent> _agentsByTargetId = new();
    private readonly DeploymentWorker _worker;
    private readonly EngineOptions _engineOptions;
    // E3: the DI-registered dispatch channel. The worker reads it (once the
    // background loop is started via StartWorkerAsync) and DeploymentService
    // (incl. the Octopus.DeployRelease child-create path) writes to it — so a
    // parent→child cascade runs end-to-end through the real gate-aware dispatch.
    private readonly Channel<KrakenDeploy.Server.Data.TenantWorkItem> _queue;
    private bool _workerStarted;
    private int _connectionCounter;

    public OrchestratorTestHarness(
        PostgresFixture postgres,
        EngineOptions? engineOptions = null,
        // E2: shorten the in-flight lease-renewal interval so a lease-loss
        // teardown test fires in milliseconds instead of the production minute.
        TimeSpan? leaseRenewInterval = null,
        // BG1 item 10: the worker's drain claim gate. Null (the default)
        // mirrors the OnPrem topology — gate off.
        KrakenDeploy.Platform.Releases.ISlotDrainGuard? slotDrainGuard = null)
    {
        ArgumentNullException.ThrowIfNull(postgres);
        _postgres = postgres;
        _engineOptions = engineOptions ?? new EngineOptions();

        var services = new ServiceCollection();

        // ── Data layer (everything except IEncryptionService) ──────────────
        services.AddKrakenDeployData(postgres.ConnectionString);

        // ── IEncryptionService — required by VariableService. The data
        //    extension method doesn't register it; Program.cs does that
        //    explicitly from the appsettings master key. Tests use a fixed
        //    in-memory key so encrypted variable round-trips are deterministic. ──
        services.AddSingleton<IEncryptionService>(_ => TestCrypto.Service(
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));

        // ── IConfiguration with no values — the orchestrator reads
        //    "Server:BaseUrl" via IConfiguration[...] which returns null when
        //    the key is missing. Octopus URL synthesis falls back to empty
        //    strings, harmless for tests. ──
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        // ── Logging: null sink, tests don't read logs. ──
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddLogging();

        // ── HTTP context accessor — used by AuditLogService. Tests have no
        //    HTTP context; audit rows carry null user. ──
        services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();

        // ── Transport layer overrides ──────────────────────────────────────
        // The data extension method already registered TimeProvider; the
        // transport bits live in Program.cs in production. We register them
        // here with one swap: IHubContext<AgentHub, IAgentHubClient> →
        // FakeAgentHubContext so target dispatches simulate inline.
        services.AddSingleton<IAgentConnectionRegistry>(_connectionRegistry);
        services.AddSingleton<IPendingSubPlanRegistry>(_subPlans);
        services.AddSingleton<ServerScriptStepRunner>();
        services.AddSingleton<DeployReleaseStepRunner>();
        services.AddSingleton<IHubContext<AgentHub, IAgentHubClient>>(
            _ => new FakeAgentHubContext(_subPlans, _connectionRegistry, _agentsByTargetId));
        services.AddSingleton<IHubContext<UiHub, IUiHubClient>>(new NullUiHubContext());
        services.AddSingleton<TargetStatusPublisher>();
        services.AddSingleton<ITargetStatusNotifier, InMemoryTargetStatusNotifier>();
        // B6 — the real cancel pusher over the fake hub: CancelDeploymentAsync
        // through the harness exercises the push, and each FakeAgent records
        // what it received in CancelPushes.
        services.AddSingleton<KrakenDeploy.Server.Data.Services.IAgentCancelPusher>(
            sp => new AgentCancelPusher(
                sp.GetRequiredService<IServiceScopeFactory>(),
                _connectionRegistry,
                sp.GetRequiredService<IHubContext<AgentHub, IAgentHubClient>>(),
                NullLogger<AgentCancelPusher>.Instance));

        _services = services.BuildServiceProvider();

        // Use the DI-registered channel so DeploymentService (and the
        // DeployRelease child-create path) enqueue onto the SAME channel the
        // worker's background loop drains.
        _queue = _services.GetRequiredService<Channel<KrakenDeploy.Server.Data.TenantWorkItem>>();
        var deployReleaseRunner = _services.GetRequiredService<DeployReleaseStepRunner>();
        deployReleaseRunner.WorkingWaitBudgetOverride = _ =>
            Task.FromResult(_engineOptions.MaxDeployReleaseWaitDuration);

        _worker = new DeploymentWorker(
            queue:                 _queue,
            registry:              _connectionRegistry,
            agentHub:              _services.GetRequiredService<IHubContext<AgentHub, IAgentHubClient>>(),
            serverRunner:          _services.GetRequiredService<ServerScriptStepRunner>(),
            deployReleaseRunner:   deployReleaseRunner,
            // Stateless bar its logger — the harness plans are target-side only,
            // so the offline path isn't exercised here, but the worker ctor
            // requires it.
            offlineBundleBuilder:  new OfflineDropBundleBuilder(
                                       NullLogger<OfflineDropBundleBuilder>.Instance),
            subPlans:              _subPlans,
            scopeFactory:          _services.GetRequiredService<IServiceScopeFactory>(),
            // M11.C diagnosis channel — the harness doesn't run the diagnosis
            // worker, so FailAsync's writes just accumulate harmlessly on this
            // unbounded channel. DiagnosisChannel exposes the written ids for
            // tests that want to assert the trigger fired.
            diagnosisChannel:      DiagnosisChannel,
            // Blue-green slot telemetry — a fresh gauge per harness; tests may
            // assert in-flight counts but the default harness ignores it.
            inFlightGauge:         Gauge,
            timeProvider:          TimeProvider.System,
            // B3 — production defaults unless a test passes short ceilings
            // (wave deadline / disconnect grace scenarios).
            engineOptions:         Microsoft.Extensions.Options.Options.Create(
                                       _engineOptions),
            logger:                NullLogger<DeploymentWorker>.Instance,
            slotDrainGuard:        slotDrainGuard)
        {
            LeaseRenewIntervalOverride = leaseRenewInterval,
            EngineSettingsOverride = _ => Task.FromResult(
                DeploymentWorker.EngineRuntimeSettings.From(_engineOptions)),
        };
    }

    /// <summary>The worker's in-flight gauge. NOTE: <see cref="RunDeploymentAsync"/>
    /// drives the worker through the test seam, which bypasses the production
    /// <c>TrackedDispatchAsync</c> wrapper — tests that assert drain behaviour
    /// wrap the call in <c>using (Gauge.Track())</c> to mirror it.</summary>
    public InFlightWorkGauge Gauge { get; } = new();

    /// <summary>The diagnosis channel the worker writes failed-deployment ids
    /// to. Tests can drain <c>DiagnosisChannel.Reader</c> to assert the
    /// trigger fired (or didn't) for a given failure path.</summary>
    public DeploymentDiagnosisChannel DiagnosisChannel { get; } = new();

    // ── Seeding helpers ─────────────────────────────────────────────────────

    public KrakenDbContext CreateContext() => _postgres.CreateContext();

    /// <summary>
    /// SC4-b: the orchestrator consults the step-type REGISTRY before
    /// dispatch — an unserved type refuses the run, registry locus drives
    /// wave sides, and a RunOnServer flag is valid only when the serving
    /// schema exposes the field. Harness scenarios seed ProcessStep rows
    /// directly without installing packages, so give the registry the same
    /// world a real install produces: script types served by kraken.script
    /// (schema exposing RunOnServer), Octopus.Manual server-side.
    /// kraken.stepgroup + octopus.deployrelease are System rows seeded by
    /// the SC2 migration. Idempotent per shared database.
    /// </summary>
    private static async Task EnsureStepCatalogAsync(KrakenDbContext db)
    {
        if (await db.StepPackages.AnyAsync(p => p.Name == "kraken.script")) { return; }

        const string scriptSchema =
            """
            {
              "id": "kraken.script",
              "title": "Run a Script",
              "version": "1.0.0",
              "properties": {
                "Octopus.Action.RunOnServer": {
                  "type": "boolean", "widget": "checkbox",
                  "label": "Run on server", "enumValues": []
                }
              }
            }
            """;

        var scriptPkg = new KrakenDeploy.Server.Core.Domain.StepPackages.StepPackage
        {
            Name         = "kraken.script",
            Version      = "1.1.0",
            Sha256       = new string('a', 64),
            ManifestJson = "{}",
            Source       = KrakenDeploy.Server.Core.Domain.StepPackages.StepPackageSource.Preinstalled,
            StepTypes    = "kraken.script,octopus.script",
        };
        var manualPkg = new KrakenDeploy.Server.Core.Domain.StepPackages.StepPackage
        {
            Name         = "octopus.manual",
            Version      = "1.2.0",
            Sha256       = new string('b', 64),
            ManifestJson = "{}",
            Source       = KrakenDeploy.Server.Core.Domain.StepPackages.StepPackageSource.Preinstalled,
            StepTypes    = "octopus.manual",
        };
        db.StepPackages.AddRange(scriptPkg, manualPkg);

        foreach (var typeId in new[] { "kraken.script", "octopus.script" })
        {
            db.StepPackageSchemas.Add(new KrakenDeploy.Server.Core.Domain.StepPackages.StepPackageSchema
            {
                StepPackageId = scriptPkg.Id,
                StepType      = typeId,
                SchemaJson    = scriptSchema,
            });
            db.StepTypes.Add(new KrakenDeploy.Server.Core.Domain.StepPackages.StepTypeEntry
            {
                TypeId                = typeId,
                DisplayName           = "Run a Script",
                ExecutionLocus        = KrakenDeploy.Server.Core.Domain.StepPackages.StepTypeExecutionLocus.AgentPackage,
                Source                = KrakenDeploy.Server.Core.Domain.StepPackages.StepTypeEntrySource.Package,
                ServingPackageName    = scriptPkg.Name,
                ServingPackageVersion = scriptPkg.Version,
            });
        }

        db.StepTypes.Add(new KrakenDeploy.Server.Core.Domain.StepPackages.StepTypeEntry
        {
            TypeId                = "octopus.manual",
            DisplayName           = "Manual Intervention",
            ExecutionLocus        = KrakenDeploy.Server.Core.Domain.StepPackages.StepTypeExecutionLocus.ServerRunner,
            Source                = KrakenDeploy.Server.Core.Domain.StepPackages.StepTypeEntrySource.Package,
            ServingPackageName    = manualPkg.Name,
            ServingPackageVersion = manualPkg.Version,
        });

        // The B8 transport round-trip fixture's test handler type — its
        // package lives only in the REAL agent's cache (staged assembly, no
        // server-side install), so the registry row is seeded here or the
        // SC4-b guard refuses those deployments before dispatch.
        var roundTripPkg = new KrakenDeploy.Server.Core.Domain.StepPackages.StepPackage
        {
            Name         = "kraken.roundtrip",
            Version      = "1.0.0",
            Sha256       = new string('c', 64),
            ManifestJson = "{}",
            Source       = KrakenDeploy.Server.Core.Domain.StepPackages.StepPackageSource.Preinstalled,
            StepTypes    = "kraken.roundtrip",
        };
        db.StepPackages.Add(roundTripPkg);
        db.StepTypes.Add(new KrakenDeploy.Server.Core.Domain.StepPackages.StepTypeEntry
        {
            TypeId                = "kraken.roundtrip",
            DisplayName           = "Round-trip test handler",
            ExecutionLocus        = KrakenDeploy.Server.Core.Domain.StepPackages.StepTypeExecutionLocus.AgentPackage,
            Source                = KrakenDeploy.Server.Core.Domain.StepPackages.StepTypeEntrySource.Package,
            ServingPackageName    = roundTripPkg.Name,
            ServingPackageVersion = roundTripPkg.Version,
        });
    }

    /// <summary>Seeds a minimum-viable Project in the Default Space.</summary>
    public async Task<Project> SeedProjectAsync(string name = "test-project")
    {
        await using var db = _postgres.CreateContext();
        await EnsureStepCatalogAsync(db);
        var p = new Project
        {
            SpaceId        = WellKnown.DefaultSpaceId,
            ProjectGroupId = await TestData.EnsureProjectGroupAsync(db, WellKnown.DefaultSpaceId),
            Name           = name,
            Slug           = name.Replace(' ', '-').ToLowerInvariant(),
            Description    = "harness test project",
        };
        db.Projects.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    public async Task<DeploymentEnvironment> SeedEnvironmentAsync(string name = "test-env")
    {
        await using var db = _postgres.CreateContext();
        var e = new DeploymentEnvironment
        {
            SpaceId   = WellKnown.DefaultSpaceId,
            Name      = name,
            Slug      = name.Replace(' ', '-').ToLowerInvariant(),
            SortOrder = 1,
        };
        db.Environments.Add(e);
        await db.SaveChangesAsync();
        return e;
    }

    /// <summary>Seeds a Tenant in the Default Space (F1 serialization-key tests
    /// need real tenant rows because the task→tenant composite FK is enforced).</summary>
    public async Task<Tenant> SeedTenantAsync(string name = "test-tenant")
    {
        await using var db = _postgres.CreateContext();
        var t = new Tenant
        {
            SpaceId = WellKnown.DefaultSpaceId,
            Name    = name,
            Slug    = $"tn-{Guid.NewGuid():N}",
        };
        db.Tenants.Add(t);
        await db.SaveChangesAsync();
        return t;
    }

    /// <summary>Seeds N targets in the Default Space with TransportMode.Reverse
    /// (the agent-side mode the orchestrator targets). Returns them in the
    /// order names were supplied.</summary>
    public Task<List<DeploymentTarget>> SeedTargetsAsync(params string[] names)
        => SeedTargetsInSpaceAsync(WellKnown.DefaultSpaceId, names);

    /// <summary>As <see cref="SeedTargetsAsync"/> but in a caller-chosen Space —
    /// for cross-Space isolation tests. Names need NOT be unique (nothing enforces
    /// MachineName uniqueness), so two Spaces can hold identically-named targets
    /// that are still distinct identities.</summary>
    public async Task<List<DeploymentTarget>> SeedTargetsInSpaceAsync(
        Guid spaceId, params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        await using var db = _postgres.CreateContext();
        var list = new List<DeploymentTarget>(names.Length);
        foreach (var n in names)
        {
            var t = new DeploymentTarget
            {
                SpaceId       = spaceId,
                Name          = n,
                Roles         = ["web"],
                TransportMode = TransportMode.Reverse,
                Status        = TargetStatus.Online,
            };
            db.DeploymentTargets.Add(t);
            list.Add(t);
        }
        await db.SaveChangesAsync();
        return list;
    }

    /// <summary>Seeds a second active Space (a real <c>spaces</c> row — the
    /// ISpaceScoped FK is Restrict, so a Space must exist before anything can be
    /// seeded into it). For cross-Space isolation tests.</summary>
    public async Task<Guid> SeedSpaceAsync(string name)
    {
        await using var db = _postgres.CreateContext();
        var space = new Space
        {
            Name   = name,
            Slug   = $"sp-{Guid.NewGuid():N}"[..12],
            Status = SpaceStatus.Active,
        };
        db.Spaces.Add(space);
        await db.SaveChangesAsync();
        return space.Id;
    }

    /// <summary>
    /// WP3-b — rewrites a target's roles. The engine reads these LIVE on every dispatch
    /// (<c>StepAppliesToTarget</c>), not from the release snapshot, so this is how a test
    /// makes a step stop applying to a task WHILE it is parked at an approval gate — the
    /// realistic version of that being an operator retagging a machine during the window.
    /// </summary>
    public async Task SetTargetRolesAsync(Guid targetId, params string[] roles)
    {
        await using var db = _postgres.CreateContext();
        var target = await db.DeploymentTargets.FirstAsync(t => t.Id == targetId);
        target.Roles = [.. roles];
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// WP3-b — forces a gate's status directly, bypassing <c>InterruptionService</c>.
    /// Used to construct states the service refuses to produce, so a guard can be tested
    /// on its own terms rather than on the current writer's happening to be careful:
    /// <c>Cancelled</c> on a still-<c>Paused</c> task, for instance.
    /// </summary>
    public async Task ForceInterruptionStatusAsync(Guid interruptionId, InterruptionStatus status)
    {
        await using var db = _postgres.CreateContext();
        var row = await db.Interruptions.IgnoreQueryFilters()
            .FirstAsync(i => i.Id == interruptionId);
        row.Status = status;
        row.ActedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    /// <summary>F2 — flips a seeded target's "Allow parallel task execution" flag,
    /// which the plan builder stamps into every sub-plan dispatched to it.</summary>
    public async Task SetAllowParallelTaskExecutionAsync(Guid targetId, bool allow)
    {
        await using var db = _postgres.CreateContext();
        await db.DeploymentTargets.IgnoreQueryFilters()
            .Where(t => t.Id == targetId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.AllowParallelTaskExecution, allow));
    }

    /// <summary>F6 — flips a seeded project's parallel-execution consent, the
    /// SOURCE side of the claim-time target exclusion for deployments.</summary>
    public async Task SetProjectAllowParallelTaskExecutionAsync(Guid projectId, bool allow)
    {
        await using var db = _postgres.CreateContext();
        await db.Projects.IgnoreQueryFilters()
            .Where(p => p.Id == projectId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.AllowParallelTaskExecution, allow));
    }

    /// <summary>F6 — flips the consent of the runbook OWNING the given run (the
    /// harness seeds a fresh runbook per run, so tests address it via the run).</summary>
    public async Task SetRunbookAllowParallelTaskExecutionForRunAsync(Guid runId, bool allow)
    {
        await using var db = _postgres.CreateContext();
        var runbookId = await db.RunbookRuns.IgnoreQueryFilters()
            .Where(r => r.Id == runId)
            .Select(r => r.RunbookId)
            .FirstAsync();
        await db.Runbooks.IgnoreQueryFilters()
            .Where(r => r.Id == runbookId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.AllowParallelTaskExecution, allow));
    }

    /// <summary>
    /// Seeds a release with the given step plans. Each step is a
    /// target-side Kraken.Script step (so wave classification puts them on
    /// the target side, exercising the new orchestrator paths). Caller
    /// chains via fluent <see cref="StepBuilder"/> instances.
    /// </summary>
    public async Task<Release> SeedReleaseAsync(
        Guid projectId, string version, params StepBuilder[] steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        await using var db = _postgres.CreateContext();
        var snapshot = new List<StepSnapshot>(steps.Length);
        for (var i = 0; i < steps.Length; i++)
        {
            snapshot.Add(steps[i].ToSnapshot(i));
        }
        var release = new Release
        {
            SpaceId                     = WellKnown.DefaultSpaceId,
            ProjectId                   = projectId,
            Version                     = version,
            ProcessSnapshot             = snapshot,
            VariableSnapshot            = [],
            VariableSnapshotUpdatedUtc  = DateTimeOffset.UtcNow,
        };
        db.Releases.Add(release);
        await db.SaveChangesAsync();
        return release;
    }

    /// <summary>
    /// Inserts a Deployment row directly (bypassing DeploymentService's
    /// lifecycle gate) with the join collection seeded for multi-target.
    /// </summary>
    public async Task<Guid> CreateDeploymentAsync(
        Guid releaseId,
        Guid environmentId,
        IReadOnlyList<DeploymentTarget> targets,
        DeploymentFailureMode failureMode = DeploymentFailureMode.BestEffort,
        // E3 transitive self-recursion coverage: seed a child (ParentTaskId set)
        // whose DeployRelease step targets an ancestor's project.
        Guid? parentTaskId = null,
        // F1 serialization-key tests: the tenant component of (project, env, tenant).
        // The tenant must already exist (composite FK) — seed via SeedTenantAsync.
        Guid? tenantId = null,
        // WP3 — a future ScheduledFor makes this a not-yet-due sibling, which is how the
        // F1 pre-gate FIFO arm is reached (and how the resume deadlock was reproduced).
        DateTimeOffset? scheduledFor = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0)
        {
            throw new ArgumentException("Need at least one target.", nameof(targets));
        }
        await using var db = _postgres.CreateContext();
        var projectId = await db.Releases.IgnoreQueryFilters()
            .Where(r => r.Id == releaseId).Select(r => r.ProjectId).FirstAsync();
        var deployment = new Deployment
        {
            SpaceId       = WellKnown.DefaultSpaceId,
            ProjectId     = projectId,
            ReleaseId     = releaseId,
            EnvironmentId = environmentId,
            TenantId      = tenantId,
            Status        = DeploymentStatus.Queued,
            FailureMode   = failureMode,
            ParentTaskId  = parentTaskId,
            ScheduledFor  = scheduledFor,
        };
        db.Deployments.Add(deployment);
        await db.SaveChangesAsync();

        AddTargetAssignments(db, deployment.Id, targets);
        await db.SaveChangesAsync();
        return deployment.Id;
    }

    /// <summary>Seeds the target-assignment join for a task, preserving order via
    /// strictly-increasing <c>AddedUtc</c> microseconds (timestamptz precision) so
    /// <c>targets[0]</c> is canonical — mirrors <c>DeploymentService.CreateAsync</c>.
    /// Shared by <see cref="CreateDeploymentAsync"/> and
    /// <see cref="CreateRunbookRunAsync"/>; the caller saves.</summary>
    private static void AddTargetAssignments(
        KrakenDbContext db, Guid taskId, IReadOnlyList<DeploymentTarget> targets)
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < targets.Count; i++)
        {
            db.TaskTargetAssignments.Add(new TaskTargetAssignment
            {
                TaskId   = taskId,
                TargetId = targets[i].Id,
                AddedUtc = now.AddMicroseconds(i),
            });
        }
    }

    /// <summary>
    /// D1 parity: inserts a Runbook + RunbookRun row directly (bypassing
    /// RunbookService.TriggerAsync), freezing <paramref name="steps"/> into the
    /// run's <see cref="RunbookRun.ProcessSnapshot"/> and seeding the target
    /// assignment join for multi-target fan-out — the runbook analogue of
    /// <see cref="CreateDeploymentAsync"/>. Drives the SAME orchestrator via
    /// <see cref="RunDeploymentAsync"/> (which kind-branches on the loaded task).
    /// </summary>
    public async Task<Guid> CreateRunbookRunAsync(
        Guid projectId,
        Guid environmentId,
        IReadOnlyList<DeploymentTarget> targets,
        IReadOnlyList<StepBuilder> steps,
        DeploymentFailureMode failureMode = DeploymentFailureMode.BestEffort)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(steps);
        if (targets.Count == 0)
        {
            throw new ArgumentException("Need at least one target.", nameof(targets));
        }

        var runId = await SeedRunbookRunAsync(projectId, environmentId, steps, failureMode);

        await using var db = _postgres.CreateContext();
        AddTargetAssignments(db, runId, targets);
        await db.SaveChangesAsync();
        return runId;
    }

    /// <summary>
    /// Seeds a Runbook + a Queued RunbookRun (optionally freezing <paramref
    /// name="steps"/> into its <see cref="RunbookRun.ProcessSnapshot"/>) and returns
    /// the run id — WITHOUT target assignments. The shared shell behind
    /// <see cref="CreateRunbookRunAsync"/> (which adds targets on top) and the
    /// bare-run seeding the dispatch-reconciler tests need (no targets, no steps).
    /// </summary>
    public async Task<Guid> SeedRunbookRunAsync(
        Guid projectId,
        Guid environmentId,
        IReadOnlyList<StepBuilder>? steps = null,
        DeploymentFailureMode failureMode = DeploymentFailureMode.BestEffort)
    {
        await using var db = _postgres.CreateContext();

        var runbook = new Runbook
        {
            SpaceId   = WellKnown.DefaultSpaceId,
            ProjectId = projectId,
            Name      = $"rb-{Guid.NewGuid():N}"[..12],
        };
        db.Add(runbook);
        await db.SaveChangesAsync();

        var snapshot = new List<StepSnapshot>();
        if (steps is not null)
        {
            for (var i = 0; i < steps.Count; i++)
            {
                snapshot.Add(steps[i].ToSnapshot(i));
            }
        }
        var run = new RunbookRun
        {
            SpaceId         = WellKnown.DefaultSpaceId,
            ProjectId       = projectId,
            EnvironmentId   = environmentId,
            RunbookId       = runbook.Id,
            Status          = DeploymentStatus.Queued,
            FailureMode     = failureMode,
            ProcessSnapshot = snapshot,
        };
        db.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    /// <summary>
    /// Registers a fake agent for <paramref name="target"/> + assigns it a
    /// fresh connection id in the registry. Returns the agent so tests can
    /// configure per-step responses + offline simulation.
    /// </summary>
    public FakeAgent ConnectFakeAgent(DeploymentTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var connectionId = $"fake-conn-{Interlocked.Increment(ref _connectionCounter)}";
        var agent = new FakeAgent { TargetId = target.Id, ConnectionId = connectionId };
        _agentsByTargetId[target.Id] = agent;
        // Add alone makes the connection dispatchable — there is no second "has registered"
        // predicate any more, because the wire contract is verified on the handshake rather
        // than in a hub method. A fake agent never calls RegisterAsync and does not need to.
        _connectionRegistry.Add(connectionId, target.Id);
        return agent;
    }

    /// <summary>Drives the orchestrator's dispatch path to terminal status.
    /// Bypasses the NodeTaskGate + queue (calls DispatchCoreAsync directly) — use
    /// <see cref="StartWorkerAsync"/> + <see cref="EnqueueAsync"/> when the gate /
    /// child-bypass path must be exercised (E3 cascade).</summary>
    public Task RunDeploymentAsync(Guid deploymentId, CancellationToken ct = default)
        => _worker.DispatchForTestAsync(deploymentId, ct);

    /// <summary>Updates the direct-construction Engine seam. The worker snapshots
    /// it again at the next wave boundary.</summary>
    public void UpdateEngineOptions(Action<EngineOptions> update) => update(_engineOptions);

    /// <summary>
    /// E3 — starts the worker's real background dispatch loop over the DI
    /// channel. Enqueued items (via <see cref="EnqueueAsync"/> and the
    /// DeployRelease child-create path) then flow through the production
    /// gate-aware dispatch (<c>NodeTaskGate</c> + child bypass). Idempotent.
    /// </summary>
    public async Task StartWorkerAsync()
    {
        if (_workerStarted)
        {
            return;
        }
        await _worker.StartAsync(CancellationToken.None);
        _workerStarted = true;
    }

    /// <summary>E3 — enqueues a top-level deployment onto the dispatch channel
    /// (single-instance account). The started worker loop picks it up.</summary>
    public ValueTask EnqueueAsync(Guid deploymentId)
        => _queue.Writer.WriteAsync(
            new KrakenDeploy.Server.Data.TenantWorkItem(Guid.Empty, deploymentId));

    /// <summary>
    /// Polls until the deployment reaches a terminal status or
    /// <paramref name="timeout"/> elapses (a deadlock detector for the E3
    /// cascade test — a real deadlock never terminates). Throws
    /// <see cref="TimeoutException"/> on timeout.
    /// </summary>
    public async Task<Deployment> WaitForTerminalAsync(Guid deploymentId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            await using (var db = _postgres.CreateContext())
            {
                var d = await db.Deployments.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == deploymentId);
                if (d is not null && d.Status.IsTerminal())
                {
                    return d;
                }
            }
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Deployment {deploymentId} did not reach a terminal status within {timeout}.");
            }
            await Task.Delay(50);
        }
    }

    /// <summary>Kind-agnostic terminal-status poll over the unified spine — works
    /// for a RunbookRun id too (unlike <see cref="WaitForTerminalAsync"/>, which
    /// queries the Deployment-only TPH set). Used by the D1 runbook DeployRelease
    /// cascade test.</summary>
    public async Task<ServerTask> WaitForServerTaskTerminalAsync(Guid taskId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            await using (var db = _postgres.CreateContext())
            {
                var t = await db.ServerTasks.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == taskId);
                if (t is not null && t.Status.IsTerminal())
                {
                    return t;
                }
            }
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Task {taskId} did not reach a terminal status within {timeout}.");
            }
            await Task.Delay(50);
        }
    }

    /// <summary>
    /// Seeds a child project (no lifecycle → the DeployRelease child-create
    /// lifecycle gate passes) plus one target-side release, returning the
    /// project id so a parent release can reference it from an
    /// <c>Octopus.DeployRelease</c> step.
    /// </summary>
    public async Task<Guid> SeedChildProjectWithReleaseAsync(string name, params StepBuilder[] steps)
    {
        var project = await SeedProjectAsync(name);
        await SeedReleaseAsync(project.Id, "1.0", steps.Length == 0
            ? [StepBuilder.Script("child-step")]
            : steps);
        return project.Id;
    }

    /// <summary>
    /// Cancels a deployment through the real
    /// <see cref="DeploymentService.CancelAsync"/> (flips Status → Cancelled +
    /// stamps CompletedUtc), exactly as the API/UI cancel paths do. Used by
    /// tests to simulate an operator cancelling a queued or in-flight
    /// deployment.
    /// </summary>
    public async Task CancelDeploymentAsync(Guid id, CancellationToken ct = default)
    {
        await using var scope = _services
            .GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<DeploymentService>();
        await svc.CancelAsync(id, CallerAuthorization.System, ct);
    }

    /// <summary>
    /// Test seam: invokes the worker's concurrent log-append helper directly.
    /// The fake agent resolves dispatches synchronously, so the orchestrator's
    /// real parallel fan-out can't be raced through normal harness flow; this
    /// lets a focused test drive <c>AppendConcurrentLogAsync</c> from genuinely
    /// parallel tasks to prove it's safe under concurrent DbContext use.
    /// </summary>
    internal static Task AppendConcurrentLogForTestAsync(
        Guid deploymentId, LogSequencer logSeq, string level, string message,
        CancellationToken ct = default)
    {
        _ = deploymentId; // the sequencer now carries the task id
        return logSeq.AppendAsync(-1, null, level, message, ct);
    }

    // ── Query helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// WP3 — polls until the task reaches <c>Paused</c> (parked at a
    /// manual-intervention gate). Distinct from the terminal waiters: Paused is
    /// deliberately NON-terminal, so <see cref="WaitForTerminalAsync"/> would spin
    /// past it until the timeout.
    /// </summary>
    public async Task<ServerTask> WaitForPausedAsync(Guid taskId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            await using (var db = _postgres.CreateContext())
            {
                var t = await db.ServerTasks.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == taskId);
                if (t is { Status: DeploymentStatus.Paused })
                {
                    return t;
                }
                if (t is not null && t.Status.IsTerminal())
                {
                    throw new InvalidOperationException(
                        $"Task {taskId} went terminal ({t.Status}) instead of pausing.");
                }
            }
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Task {taskId} did not pause within {timeout}.");
            }
            await Task.Delay(50);
        }
    }

    /// <summary>WP3 — the task's manual-intervention gates, oldest first.</summary>
    public async Task<List<Interruption>> GetInterruptionsAsync(Guid taskId)
    {
        await using var db = _postgres.CreateContext();
        return await db.Interruptions.IgnoreQueryFilters()
            .Where(i => i.TaskId == taskId)
            .OrderBy(i => i.StepIndex)
            .ToListAsync();
    }

    /// <summary>
    /// WP3 — resolves a gate directly in the DB, standing in for a human response
    /// without the service's authorization layer (that is covered by its own tests).
    /// <para>
    /// Does NOT enqueue a wake-up — the caller decides how to resume, which matters:
    /// <see cref="RunDeploymentAsync"/> bypasses the production gate path, so a test
    /// that wants the REAL resume (ProbeGateAsync, the NodeTaskGate, the F1 pre-gate
    /// skip) must go through <see cref="StartWorkerAsync"/> +
    /// <see cref="EnqueueAsync"/>. An earlier version of this comment claimed it
    /// enqueued, which is why no test exercised that path.
    /// </para>
    /// </summary>
    public async Task ResolveInterruptionAsync(
        Guid interruptionId, InterruptionStatus resolution, string? notes = null,
        string actedBy = "test-user")
    {
        await using (var db = _postgres.CreateContext())
        {
            var i = await db.Interruptions.IgnoreQueryFilters()
                .FirstAsync(x => x.Id == interruptionId);
            i.Status         = resolution;
            i.Notes          = notes;
            i.ActedByDisplay = actedBy;
            i.ActedUtc       = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// WP3 — seeds a SENSITIVE project variable, so a test can prove the gate's
    /// instructions are redacted rather than storing the decrypted value.
    /// </summary>
    /// <summary>
    /// WP3-b — stamps a variable into a RELEASE's variable snapshot, which is what a
    /// deployment actually resolves against.
    /// <para>
    /// <see cref="SeedSensitiveVariableAsync"/> creates a project variable, but
    /// <see cref="SeedReleaseAsync"/> seeds <c>VariableSnapshot = []</c> — so a project
    /// variable alone NEVER reaches a deployment and every <c>#{Name}</c> in a step's config
    /// stays literal. That silently made assertions of the form "the rendered text does not
    /// contain the secret" pass for the wrong reason: nothing was rendered. Any test about
    /// substitution or masking must use this.
    /// </para>
    /// </summary>
    public async Task SnapshotVariableAsync(
        Guid releaseId, Guid projectId, string name, string value, bool sensitive = false)
    {
        await using var scope = _services.GetRequiredService<IServiceScopeFactory>()
            .CreateAsyncScope();
        var vars = scope.ServiceProvider
            .GetRequiredService<KrakenDeploy.Server.Data.Services.VariableService>();
        var releases = scope.ServiceProvider
            .GetRequiredService<KrakenDeploy.Server.Data.Services.ReleaseService>();

        // Both halves through the REAL services. A hand-built VariableSnapshot is not a
        // shortcut here: ReleaseService.ToSnapshot copies Variable.Value verbatim, and for a
        // Sensitive variable that value is CIPHERTEXT (encrypted at rest by
        // VariableService). Writing the plaintext into the snapshot makes the planner's
        // decrypt throw and the deployment fail before it ever reaches the gate.
        await vars.CreateVariableAsync(
            projectId, name, value,
            sensitive
                ? KrakenDeploy.Server.Core.Domain.Variables.VariableType.Sensitive
                : KrakenDeploy.Server.Core.Domain.Variables.VariableType.Text,
            scope: new KrakenDeploy.Server.Core.Domain.Variables.VariableScope(),
            caller: CallerAuthorization.System);
        await releases.UpdateVariablesAsync(releaseId, CallerAuthorization.System);
    }

    public async Task SeedSensitiveVariableAsync(Guid projectId, string name, string value)
    {
        // Through the real service, so the value is encrypted exactly as production
        // stores it and the project's variable set is created/resolved the same way.
        await using var scope = _services.GetRequiredService<IServiceScopeFactory>()
            .CreateAsyncScope();
        var vars = scope.ServiceProvider
            .GetRequiredService<KrakenDeploy.Server.Data.Services.VariableService>();
        await vars.CreateVariableAsync(
            projectId, name, value,
            KrakenDeploy.Server.Core.Domain.Variables.VariableType.Sensitive,
            scope: new KrakenDeploy.Server.Core.Domain.Variables.VariableScope(),
            caller: CallerAuthorization.System);
    }

    /// <summary>
    /// WP3-c — seeds a plain (non-sensitive) PROJECT variable through the real
    /// service and returns its id. Runbook runs resolve project variables LIVE
    /// (no snapshot), so this — unlike <see cref="SnapshotVariableAsync"/>, which
    /// needs a release — is how a test hands a runbook run a <c>#{Name}</c>, and
    /// <see cref="UpdateVariableValueAsync"/> is how it changes it mid-pause.
    /// StringArray values accept the UI's comma-separated form ("a,b,c").
    /// </summary>
    public async Task<Guid> SeedVariableAsync(
        Guid projectId, string name, string value,
        KrakenDeploy.Server.Core.Domain.Variables.VariableType type
            = KrakenDeploy.Server.Core.Domain.Variables.VariableType.Text)
    {
        await using var scope = _services.GetRequiredService<IServiceScopeFactory>()
            .CreateAsyncScope();
        var vars = scope.ServiceProvider
            .GetRequiredService<KrakenDeploy.Server.Data.Services.VariableService>();
        var variable = await vars.CreateVariableAsync(
            projectId, name, value, type,
            scope: new KrakenDeploy.Server.Core.Domain.Variables.VariableScope(),
            caller: CallerAuthorization.System);
        return variable.Id;
    }

    /// <summary>WP3-c — updates a variable's value through the real service, keeping
    /// its name/type/scope. The live-resolution edit a runbook approval window sees.</summary>
    public async Task UpdateVariableValueAsync(
        Guid variableId, string name, string value,
        KrakenDeploy.Server.Core.Domain.Variables.VariableType type
            = KrakenDeploy.Server.Core.Domain.Variables.VariableType.Text)
    {
        await using var scope = _services.GetRequiredService<IServiceScopeFactory>()
            .CreateAsyncScope();
        var vars = scope.ServiceProvider
            .GetRequiredService<KrakenDeploy.Server.Data.Services.VariableService>();
        await vars.UpdateVariableAsync(
            variableId, name, value, type,
            scope: new KrakenDeploy.Server.Core.Domain.Variables.VariableScope(),
            caller: CallerAuthorization.System);
    }

    /// <summary>WP3-a — seeds an ordinary named team, Space-scoped, so a test can prove
    /// an approver list resolves (and that a team of ANOTHER Space does not).</summary>
    public async Task<Guid> SeedTeamAsync(string name, Guid spaceId)
    {
        await using var db = _postgres.CreateContext();
        var team = new KrakenDeploy.Server.Core.Domain.Security.Team
        {
            SpaceId = spaceId,
            Name    = name,
        };
        db.Add(team);
        await db.SaveChangesAsync();
        return team.Id;
    }

    /// <summary>WP3 — seeds an "Everyone" team, which every authenticated user belongs
    /// to and which the gate must therefore REFUSE as a vacuous restriction.</summary>
    public async Task<Guid> SeedEveryoneTeamAsync()
    {
        await using var db = _postgres.CreateContext();
        var team = new KrakenDeploy.Server.Core.Domain.Security.Team
        {
            SpaceId        = null,
            Name           = $"Everyone-{Guid.NewGuid():N}"[..20],
            IsEveryoneTeam = true,
        };
        db.Add(team);
        await db.SaveChangesAsync();
        return team.Id;
    }

    /// <summary>WP3 — seeds an ACTIVE deployment freeze over (project, environment), to
    /// prove a resume is exempt from it.</summary>
    /// <summary>
    /// Creates a currently-active freeze covering one project + environment.
    /// <para>
    /// WP3-b — goes through <c>DeploymentFreezeService</c> rather than inserting the row
    /// directly, because the service keeps a 30 s per-Space TTL cache that only its own
    /// CRUD invalidates. Inserting directly left the cache holding the "no freezes" answer
    /// the FIRST dispatch had already populated, so any test that seeded a freeze and then
    /// re-dispatched within 30 s exercised a stale cache instead of the freeze gate — and
    /// passed whichever way the gate behaved.
    /// </para>
    /// </summary>
    public async Task SeedFreezeAsync(Guid projectId, Guid environmentId)
    {
        await using var scope = _services.GetRequiredService<IServiceScopeFactory>()
            .CreateAsyncScope();
        var freezes = scope.ServiceProvider
            .GetRequiredService<KrakenDeploy.Server.Data.Services.DeploymentFreezeService>();
        await freezes.CreateAsync(new KrakenDeploy.Server.Core.Domain.Freezes.DeploymentFreeze
        {
            SpaceId        = WellKnown.DefaultSpaceId,
            Name           = $"freeze-{Guid.NewGuid():N}"[..14],
            StartUtc       = DateTimeOffset.UtcNow.AddMinutes(-5),
            EndUtc         = DateTimeOffset.UtcNow.AddHours(4),
            ProjectIds     = [projectId],
            EnvironmentIds = [environmentId],
        });
    }

    /// <summary>
    /// WP3 — resolves a gate and resumes through the REAL dispatch path: enqueue onto
    /// the shared channel so the started worker runs
    /// <c>GateThenDispatchCoreAsync</c> → <c>ProbeGateAsync</c> → <c>NodeTaskGate</c> →
    /// <c>DispatchCoreAsync</c>, exactly as <c>InterruptionService.SignalResumeAsync</c>
    /// does in production. <see cref="RunDeploymentAsync"/> skips all of that, which is
    /// why the pre-gate deadlock and the duplicate-wake-up hard-fail were invisible.
    /// Requires <see cref="StartWorkerAsync"/>.
    /// </summary>
    public async Task ResolveAndSignalAsync(
        Guid interruptionId, InterruptionStatus resolution, string? notes = null,
        string actedBy = "test-user")
    {
        Guid taskId;
        await using (var db = _postgres.CreateContext())
        {
            var i = await db.Interruptions.IgnoreQueryFilters()
                .FirstAsync(x => x.Id == interruptionId);
            i.Status         = resolution;
            i.Notes          = notes;
            i.ActedByDisplay = actedBy;
            i.ActedUtc       = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            taskId = i.TaskId;
        }
        await EnqueueAsync(taskId);
    }

    /// <summary>WP3 — the task's output variables, to prove the pause checkpoint really
    /// carried them across a resume.</summary>
    public async Task<List<TaskOutputVariable>> GetOutputVariablesAsync(Guid taskId)
    {
        await using var db = _postgres.CreateContext();
        return await db.TaskOutputVariables.IgnoreQueryFilters()
            .Where(v => v.TaskId == taskId)
            .ToListAsync();
    }

    /// <summary>WP3 — the raw encrypted checkpoint, so a test can assert it is cleared
    /// (or corrupt it deliberately).</summary>
    public async Task<string?> GetPauseCheckpointAsync(Guid taskId)
    {
        await using var db = _postgres.CreateContext();
        return await db.ServerTasks.IgnoreQueryFilters()
            .Where(t => t.Id == taskId)
            .Select(t => t.PauseCheckpointEncrypted)
            .FirstAsync();
    }

    public async Task<Deployment> GetDeploymentAsync(Guid id)
    {
        await using var db = _postgres.CreateContext();
        var d = await db.Deployments.FirstOrDefaultAsync(d => d.Id == id);
        return d ?? throw new InvalidOperationException($"Deployment {id} not found.");
    }

    /// <summary>Kind-agnostic getter over the unified spine — resolves EITHER a
    /// deployment or a runbook run (the TPH subtype materialises). Used by the
    /// D1 runbook-parity tests, which drive a RunbookRun id through the same
    /// worker.</summary>
    public async Task<ServerTask> GetServerTaskAsync(Guid id)
    {
        await using var db = _postgres.CreateContext();
        var t = await db.ServerTasks.FirstOrDefaultAsync(t => t.Id == id);
        return t ?? throw new InvalidOperationException($"Task {id} not found.");
    }

    /// <summary>The audit event-type strings recorded against a task id, in
    /// occurrence order. Lets parity tests assert the kind-branched audit
    /// vocabulary (RunbookRun.* vs Deployment.*).</summary>
    public async Task<List<string>> GetAuditEventTypesAsync(Guid subjectId)
    {
        await using var db = _postgres.CreateContext();
        return await db.AuditEntries.IgnoreQueryFilters()
            .Where(e => e.SubjectId == subjectId.ToString())
            .OrderBy(e => e.OccurredUtc)
            .Select(e => e.EventType)
            .ToListAsync();
    }

    public async Task<List<TaskStepOutcome>> GetOutcomesAsync(Guid deploymentId)
    {
        await using var db = _postgres.CreateContext();
        return await db.TaskStepOutcomes
            .Where(o => o.TaskId == deploymentId)
            .OrderBy(o => o.StepIndex).ThenBy(o => o.TargetId)
            .ToListAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_workerStarted)
        {
            // Stop the background loop before tearing down the container so an
            // in-flight fire-and-forget dispatch doesn't touch a disposed scope.
            try { await _worker.StopAsync(CancellationToken.None); }
            catch (OperationCanceledException) { }
        }
        await _services.DisposeAsync();
    }
}

// ── Builders ────────────────────────────────────────────────────────────────

/// <summary>
/// Fluent builder for a step in a seeded release. Defaults to a target-side
/// Kraken.Script step with empty ScriptBody — the fake agent doesn't run
/// the script, it just returns the configured FakeStepResponse.
/// </summary>
public sealed class StepBuilder
{
    public string Name { get; init; } = "step";
    public string StepType { get; init; } = "Octopus.Script";
    public bool Required { get; init; } = true;
    public Guid? ParentStepId { get; init; }
    public Guid Id { get; init; } = Guid.NewGuid();
    public Dictionary<string, string>? Config { get; init; }
    // B3 — explicit per-step timeout / retry knobs for deadline + retry tests.
    public int TimeoutSeconds { get; init; }
    public int MaxRetries { get; init; }
    // B4 — Run Condition (e.g. Always for cleanup steps consuming a failed
    // step's outputs).
    public KrakenDeploy.Execution.StepCondition Condition { get; init; }
        = KrakenDeploy.Execution.StepCondition.Success;

    // B8 — step-package pin. The REAL agent resolves its handler exclusively
    // from this pin (no hardcoded fallback); the fake-agent harness ignores it.
    public string? StepPackageName { get; init; }
    public string? StepPackageVersion { get; init; }

    // D3 — control-flow flags are typed columns now (promoted from jsonb Config).
    // RunOnServer routes a leaf step server-side; MaxParallelism/ForEach* live on
    // a Kraken.StepGroup. ToSnapshot maps them onto the typed StepSnapshot columns.
    /// <summary>
    /// WP3-b — the step's role filter. Empty (the default) means "applies to every
    /// target", which is why most tests can ignore it. Set it to exercise
    /// <c>StepAppliesToTarget</c>, which reads the target's LIVE roles rather than the
    /// frozen snapshot — the mechanism by which a step can stop applying between a pause
    /// and its resume.
    /// </summary>
    public List<string>? TargetRoles { get; init; }

    public bool RunOnServer { get; init; }
    public int? MaxParallelism { get; init; }
    public string? ForEachCollection { get; init; }
    public bool ForEachParallel { get; init; }

    public static StepBuilder Script(
        string name,
        bool required = true,
        KrakenDeploy.Execution.StepCondition condition
            = KrakenDeploy.Execution.StepCondition.Success)
        => new()
        {
            Name      = name,
            StepType  = "Octopus.Script",
            Required  = required,
            Condition = condition,
        };

    /// <summary>A "Run on Server" script step (typed <c>RunOnServer = true</c>,
    /// D3 — promoted from the <c>Octopus.Action.RunOnServer</c> Config key). The
    /// wave partitioner classifies it server-side, so it runs in-process on the
    /// orchestrator, NOT on the target agent — the D1 security fix for runbook
    /// runs (which previously executed RunOnServer steps on the target because
    /// the partitioner never ran). WP3-c: pass <paramref name="scriptBody"/> when
    /// the step must actually EXECUTE and succeed — server scripts run for real,
    /// and an empty body is a runner error.</summary>
    public static StepBuilder ServerScript(
        string name,
        bool required = true,
        KrakenDeploy.Execution.StepCondition condition
            = KrakenDeploy.Execution.StepCondition.Success,
        string? scriptBody = null)
        => new()
        {
            Name        = name,
            StepType    = "Octopus.Script",
            Required    = required,
            RunOnServer = true,
            Condition   = condition,
            Config      = scriptBody is null
                ? null
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Octopus.Action.Script.ScriptBody"] = scriptBody,
                },
        };

    /// <summary>
    /// A server-side <c>Octopus.DeployRelease</c> step targeting
    /// <paramref name="childProjectId"/> (by GUID). Used by the E3 cascade /
    /// ceiling tests. <paramref name="timeoutSeconds"/> defaults to 0 so the
    /// Engine <c>MaxDeployReleaseWaitDuration</c> ceiling governs the wait.
    /// </summary>
    public static StepBuilder DeployRelease(
        string name, Guid childProjectId, bool required = true, int timeoutSeconds = 0, int maxRetries = 0)
        => new()
        {
            Name           = name,
            StepType       = KrakenDeploy.Server.Transport.DeployReleaseStepRunner.StepType,
            Required       = required,
            TimeoutSeconds = timeoutSeconds,
            MaxRetries     = maxRetries,
            Config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [KrakenDeploy.Server.Transport.OctopusDeployReleaseConfigKeys.ProjectId]
                    = childProjectId.ToString(),
            },
        };

    /// <summary>
    /// WP3 — a manual-intervention gate. <c>Octopus.Manual</c> is in
    /// <c>WavePartitioner.ServerOnlyStepTypes</c>, so it is intrinsically server-side
    /// (no <c>RunOnServer</c> flag needed) and the whole task pauses before its wave.
    /// </summary>
    public static StepBuilder Manual(
        string name,
        bool required = true,
        string? instructions = "Approve this change.",
        string? responsibleTeamIds = null,
        double? timeoutHours = null,
        KrakenDeploy.Execution.StepCondition condition
            = KrakenDeploy.Execution.StepCondition.Success,
        // Deliberately settable so a test can prove the CASE-INSENSITIVE read: a gate
        // whose approver key is typed in the wrong case must not silently widen to
        // "anyone".
        string? responsibleTeamIdsKey = null,
        // WP3-b — role filter, so a test can make the gate stop applying mid-pause.
        List<string>? targetRoles = null)
    {
        var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (instructions is not null)
        {
            config[KrakenDeploy.Contracts.Steps.ManualInterventionConfigKeys.Instructions] = instructions;
        }
        if (responsibleTeamIds is not null)
        {
            config[responsibleTeamIdsKey
                ?? KrakenDeploy.Contracts.Steps.ManualInterventionConfigKeys.ResponsibleTeamIds]
                = responsibleTeamIds;
        }
        if (timeoutHours is { } hours)
        {
            config[KrakenDeploy.Contracts.Steps.ManualInterventionConfigKeys.TimeoutHours]
                = hours.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return new StepBuilder
        {
            Name        = name,
            StepType    = KrakenDeploy.Contracts.Steps.ManualInterventionConfigKeys.StepType,
            Required    = required,
            Config      = config,
            Condition   = condition,
            TargetRoles = targetRoles,
        };
    }

    /// <summary>A Step Group. <paramref name="maxParallelism"/> flows to the
    /// typed column verbatim — pass a non-positive value to exercise the D3
    /// "malformed rolling window → batching disabled" runtime warning path.</summary>
    public static StepBuilder StepGroup(string name, int? maxParallelism = null)
        => new()
        {
            Name           = name,
            StepType       = KrakenStepTypes.StepGroup,
            Required       = false,
            MaxParallelism = maxParallelism,
        };

    public StepBuilder InGroup(Guid parentId)
        => new()
        {
            Id                = Id,
            Name              = Name,
            StepType          = StepType,
            Required          = Required,
            Config            = Config,
            ParentStepId      = parentId,
            // D3 — preserve the typed control-flow flags when re-parenting.
            RunOnServer       = RunOnServer,
            MaxParallelism    = MaxParallelism,
            ForEachCollection = ForEachCollection,
            ForEachParallel   = ForEachParallel,
        };

    internal StepSnapshot ToSnapshot(int sortOrder) => new()
    {
        Id                 = Id,
        Name               = Name,
        StepType           = StepType,
        Required           = Required,
        SortOrder          = sortOrder,
        ParentStepId       = ParentStepId,
        Config             = Config ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        PackageId          = "",
        PackageVersion     = "",
        TimeoutSeconds     = TimeoutSeconds,
        MaxRetries         = MaxRetries,
        Condition          = Condition,
        TargetRoles        = TargetRoles ?? [],
        StepPackageName    = StepPackageName,
        StepPackageVersion = StepPackageVersion,
        // D3 — map the typed control-flow flags onto the snapshot columns the
        // engine reads (WavePartitioner.RunOnServer, RollingWindowResolver.
        // MaxParallelism, DeploymentPlanFlattener.ForEach*).
        RunOnServer        = RunOnServer,
        MaxParallelism     = MaxParallelism,
        ForEachCollection  = ForEachCollection,
        ForEachParallel    = ForEachParallel,
    };
}

// ── Inert UI hub fake ───────────────────────────────────────────────────────

internal sealed class NullUiHubContext : IHubContext<UiHub, IUiHubClient>
{
    public IHubClients<IUiHubClient> Clients { get; } = new NullHubClients();
    public IGroupManager Groups => throw new NotSupportedException();

    private sealed class NullHubClients : IHubClients<IUiHubClient>
    {
        private readonly IUiHubClient _sink = new NullUiHubClient();
        public IUiHubClient All => _sink;
        public IUiHubClient AllExcept(IReadOnlyList<string> excluded) => _sink;
        public IUiHubClient Client(string connectionId) => _sink;
        public IUiHubClient Clients(IReadOnlyList<string> connectionIds) => _sink;
        public IUiHubClient Group(string groupName) => _sink;
        public IUiHubClient GroupExcept(string groupName, IReadOnlyList<string> excluded) => _sink;
        public IUiHubClient Groups(IEnumerable<string> groupNames) => _sink;
        public IUiHubClient Groups(IReadOnlyList<string> groupNames) => _sink;
        public IUiHubClient User(string userId) => _sink;
        public IUiHubClient Users(IEnumerable<string> userIds) => _sink;
        public IUiHubClient Users(IReadOnlyList<string> userIds) => _sink;
    }

    private sealed class NullUiHubClient : IUiHubClient
    {
        public Task TargetStatusChangedAsync(Guid targetId, string status, DateTimeOffset? lastSeenUtc)
            => Task.CompletedTask;
        public Task DeploymentLogAppendedAsync(
            Guid deploymentId, int sequence, DateTimeOffset timestamp,
            string level, string message) => Task.CompletedTask;
        public Task DeploymentStatusChangedAsync(Guid deploymentId, string status)
            => Task.CompletedTask;
    }
}
