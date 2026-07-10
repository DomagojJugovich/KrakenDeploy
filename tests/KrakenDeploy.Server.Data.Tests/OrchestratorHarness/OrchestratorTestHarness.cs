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
using KrakenDeploy.Server.Core.Domain.Targets;
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
    private int _connectionCounter;

    public OrchestratorTestHarness(PostgresFixture postgres)
    {
        ArgumentNullException.ThrowIfNull(postgres);
        _postgres = postgres;

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

        _services = services.BuildServiceProvider();

        _worker = new DeploymentWorker(
            queue:                 Channel.CreateUnbounded<KrakenDeploy.Server.Data.TenantWorkItem>(),
            registry:              _connectionRegistry,
            agentHub:              _services.GetRequiredService<IHubContext<AgentHub, IAgentHubClient>>(),
            serverRunner:          _services.GetRequiredService<ServerScriptStepRunner>(),
            deployReleaseRunner:   _services.GetRequiredService<DeployReleaseStepRunner>(),
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
            inFlightGauge:         new InFlightWorkGauge(),
            logger:                NullLogger<DeploymentWorker>.Instance);
    }

    /// <summary>The diagnosis channel the worker writes failed-deployment ids
    /// to. Tests can drain <c>DiagnosisChannel.Reader</c> to assert the
    /// trigger fired (or didn't) for a given failure path.</summary>
    public DeploymentDiagnosisChannel DiagnosisChannel { get; } = new();

    // ── Seeding helpers ─────────────────────────────────────────────────────

    public KrakenDbContext CreateContext() => _postgres.CreateContext();

    /// <summary>Seeds a minimum-viable Project in the Default Space.</summary>
    public async Task<Project> SeedProjectAsync(string name = "test-project")
    {
        await using var db = _postgres.CreateContext();
        var p = new Project
        {
            SpaceId     = WellKnown.DefaultSpaceId,
            Name        = name,
            Slug        = name.Replace(' ', '-').ToLowerInvariant(),
            Description = "harness test project",
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

    /// <summary>Seeds N targets in the Default Space with TransportMode.Reverse
    /// (the agent-side mode the orchestrator targets). Returns them in the
    /// order names were supplied.</summary>
    public async Task<List<DeploymentTarget>> SeedTargetsAsync(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        await using var db = _postgres.CreateContext();
        var list = new List<DeploymentTarget>(names.Length);
        foreach (var n in names)
        {
            var t = new DeploymentTarget
            {
                SpaceId       = WellKnown.DefaultSpaceId,
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
        DeploymentFailureMode failureMode = DeploymentFailureMode.BestEffort)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0)
        {
            throw new ArgumentException("Need at least one target.", nameof(targets));
        }
        await using var db = _postgres.CreateContext();
        var deployment = new Deployment
        {
            SpaceId       = WellKnown.DefaultSpaceId,
            ReleaseId     = releaseId,
            EnvironmentId = environmentId,
            Status        = DeploymentStatus.Queued,
            FailureMode   = failureMode,
        };
        db.Deployments.Add(deployment);
        await db.SaveChangesAsync();

        // Mirror DeploymentService.CreateAsync: strictly increasing AddedUtc
        // microseconds (timestamptz precision) preserve assignment order
        // (targets[0] = canonical).
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < targets.Count; i++)
        {
            db.DeploymentTargetAssignments.Add(new DeploymentTargetAssignment
            {
                DeploymentId = deployment.Id,
                TargetId     = targets[i].Id,
                AddedUtc     = now.AddMicroseconds(i),
            });
        }
        await db.SaveChangesAsync();
        return deployment.Id;
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
        _connectionRegistry.Add(connectionId, target.Id);
        return agent;
    }

    /// <summary>Drives the orchestrator's dispatch path to terminal status.</summary>
    public Task RunDeploymentAsync(Guid deploymentId, CancellationToken ct = default)
        => _worker.DispatchForTestAsync(deploymentId, ct);

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
        await svc.CancelAsync(id, ct);
    }

    /// <summary>
    /// Test seam: invokes the worker's concurrent log-append helper directly.
    /// The fake agent resolves dispatches synchronously, so the orchestrator's
    /// real parallel fan-out can't be raced through normal harness flow; this
    /// lets a focused test drive <c>AppendConcurrentLogAsync</c> from genuinely
    /// parallel tasks to prove it's safe under concurrent DbContext use.
    /// </summary>
    internal Task AppendConcurrentLogForTestAsync(
        Guid deploymentId, LogSequencer logSeq, string level, string message,
        CancellationToken ct = default)
        => _worker.AppendConcurrentLogAsync(deploymentId, logSeq, level, message, ct);

    // ── Query helpers ───────────────────────────────────────────────────────

    public async Task<Deployment> GetDeploymentAsync(Guid id)
    {
        await using var db = _postgres.CreateContext();
        var d = await db.Deployments.FirstOrDefaultAsync(d => d.Id == id);
        return d ?? throw new InvalidOperationException($"Deployment {id} not found.");
    }

    public async Task<List<DeploymentStepOutcome>> GetOutcomesAsync(Guid deploymentId)
    {
        await using var db = _postgres.CreateContext();
        return await db.DeploymentStepOutcomes
            .Where(o => o.DeploymentId == deploymentId)
            .OrderBy(o => o.StepIndex).ThenBy(o => o.TargetId)
            .ToListAsync();
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();
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

    public static StepBuilder Script(string name, bool required = true)
        => new() { Name = name, StepType = "Octopus.Script", Required = required };

    public static StepBuilder StepGroup(string name, int? maxParallelism = null)
    {
        var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (maxParallelism is > 0)
        {
            config["Octopus.Action.MaxParallelism"] =
                maxParallelism.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return new StepBuilder
        {
            Name     = name,
            StepType = KrakenStepTypes.StepGroup,
            Required = false,
            Config   = config,
        };
    }

    public StepBuilder InGroup(Guid parentId)
        => new()
        {
            Id           = Id,
            Name         = Name,
            StepType     = StepType,
            Required     = Required,
            Config       = Config,
            ParentStepId = parentId,
        };

    internal StepSnapshot ToSnapshot(int sortOrder) => new()
    {
        Id             = Id,
        Name           = Name,
        StepType       = StepType,
        Required       = Required,
        SortOrder      = sortOrder,
        ParentStepId   = ParentStepId,
        Config         = Config ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        PackageId      = "",
        PackageVersion = "",
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
