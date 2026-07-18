using System.Security.Claims;
using FluentAssertions;
using KrakenDeploy.Contracts.Adhoc;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Runbooks;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;
using KrakenDeploy.Server.Transport;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// E1 (execution-engine audit 2026-07-16) — the hub's DB-fallback finalize in
/// <see cref="AgentHub.CompleteDeploymentAsync"/> must be reachable ONLY by the
/// runbook-run hand-off model, and never while the dispatch lease is live.
/// <para>
/// The audited defect: a server restarts mid-deployment faster than the 5-minute
/// lease. The worker's in-memory wave state dies with the old process; the boot
/// reconciler defers to the still-live lease; the agent's buffered WAVE
/// completion flushes from its outbox into the FRESH process, where
/// <c>subPlans.RouteCompletion</c> finds no open slot (<c>NoPendingSubPlan</c>);
/// the fallback then wrote the WHOLE deployment <c>Succeeded</c> although its
/// remaining waves never ran (the <c>!IsTerminal</c> guard passes for a
/// <c>Running</c> row). A single assigned agent could flip a farm-wide verdict.
/// </para>
/// <para>
/// The fix restricts the fallback finalize to
/// <see cref="ServerTaskKind.RunbookRun"/> AND refuses it while
/// <c>ClaimedBy</c>/lease is live. These tests drive the hub through the fallback
/// path (a fresh, empty <see cref="PendingSubPlanRegistry"/> routes every
/// completion to <c>NoPendingSubPlan</c>) and assert the outcome per kind + lease.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class AgentHubFallbackFinalizeTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    private const string DevMasterKey = "S3Jha2VuRGVwbG95RGV2TWFzdGVyS2V5MzJCeXRlcyE=";

    [Fact]
    public async Task Deployment_fallback_does_not_finalize_a_running_deployment_with_a_live_lease()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var (deploymentId, targetId) = await SeedRunningDeploymentAsync(harness, leaseMinutes: 5);

        // A buffered wave completion arriving at a fresh process: no sub-plan slot
        // is registered, so RouteCompletion → NoPendingSubPlan → the fallback.
        await BuildHub(targetId)
            .CompleteDeploymentAsync(deploymentId, Guid.NewGuid(), success: true, errorMessage: null);

        var dep = await GetTaskAsync(harness, deploymentId);
        dep.Status.Should().Be(DeploymentStatus.Running,
            "a buffered deployment-wave completion with no open orchestrator slot must NOT finalize " +
            "the deployment — its remaining waves never ran; the orchestrator finalizes deployments");
        dep.CompletedUtc.Should().BeNull();
    }

    [Fact]
    public async Task Deployment_fallback_drops_even_without_a_lease()
    {
        // Belt-and-suspenders: the kind check drops a deployment completion
        // regardless of lease state — only the orchestrator finalizes a deployment.
        await using var harness = new OrchestratorTestHarness(postgres);
        var (deploymentId, targetId) = await SeedRunningDeploymentAsync(harness, leaseMinutes: null);

        await BuildHub(targetId)
            .CompleteDeploymentAsync(deploymentId, Guid.NewGuid(), success: true, errorMessage: null);

        var dep = await GetTaskAsync(harness, deploymentId);
        dep.Status.Should().Be(DeploymentStatus.Running,
            "a deployment-kind completion is dropped by the hub fallback irrespective of lease");
    }

    [Fact]
    public async Task RunbookRun_fallback_finalizes_a_handed_off_run()
    {
        // The one LEGITIMATE fallback user: a runbook run releases its lease at
        // hand-off and the hub finalizes on the agent's completion callback.
        await using var harness = new OrchestratorTestHarness(postgres);
        var (runId, targetId) = await SeedRunningRunbookRunAsync(harness, leaseMinutes: null);

        await BuildHub(targetId)
            .CompleteDeploymentAsync(runId, Guid.NewGuid(), success: true, errorMessage: null);

        var run = await GetTaskAsync(harness, runId);
        run.Status.Should().Be(DeploymentStatus.Succeeded,
            "a handed-off runbook run (lease released) is legitimately finalized by the hub fallback");
        run.CompletedUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task RunbookRun_fallback_refuses_a_run_with_a_live_lease()
    {
        // A live lease means the run is still worker-owned (pre-hand-off) — a
        // completion now is not the legitimate post-hand-off callback.
        await using var harness = new OrchestratorTestHarness(postgres);
        var (runId, targetId) = await SeedRunningRunbookRunAsync(harness, leaseMinutes: 5);

        await BuildHub(targetId)
            .CompleteDeploymentAsync(runId, Guid.NewGuid(), success: true, errorMessage: null);

        var run = await GetTaskAsync(harness, runId);
        run.Status.Should().Be(DeploymentStatus.Running,
            "a live lease means the runbook run is still worker-owned; the fallback must refuse it");
    }

    // ── Seeding ───────────────────────────────────────────────────────────────

    private static async Task<(Guid TaskId, Guid TargetId)> SeedRunningDeploymentAsync(
        OrchestratorTestHarness harness, int? leaseMinutes)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var project = await harness.SeedProjectAsync($"fb-p-{tag}");
        var env = await harness.SeedEnvironmentAsync($"fb-e-{tag}");
        var targets = await harness.SeedTargetsAsync($"fb-t-{tag}");
        var release = await harness.SeedReleaseAsync(project.Id, "1.0.0");
        var deploymentId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        await SetRunningWithLeaseAsync(harness, deploymentId, leaseMinutes);
        return (deploymentId, targets[0].Id);
    }

    private static async Task<(Guid TaskId, Guid TargetId)> SeedRunningRunbookRunAsync(
        OrchestratorTestHarness harness, int? leaseMinutes)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = harness.CreateContext();

        var env = new DeploymentEnvironment
        {
            SpaceId = WellKnown.DefaultSpaceId, Name = $"rr-e-{tag}", Slug = $"rr-e-{tag}", SortOrder = 1,
        };
        db.Environments.Add(env);
        var project = new Project
        {
            SpaceId = WellKnown.DefaultSpaceId, Name = $"rr-p-{tag}", Slug = $"rr-p-{tag}",
            ProjectGroupId = await TestData.EnsureProjectGroupAsync(db, WellKnown.DefaultSpaceId),
        };
        db.Projects.Add(project);
        var target = new DeploymentTarget
        {
            SpaceId = WellKnown.DefaultSpaceId, Name = $"rr-t-{tag}", Roles = ["web"],
            TransportMode = TransportMode.Reverse, Status = TargetStatus.Online,
        };
        db.DeploymentTargets.Add(target);
        await db.SaveChangesAsync();

        var runbook = new Runbook
        {
            SpaceId = WellKnown.DefaultSpaceId, ProjectId = project.Id, Name = $"rr-rb-{tag}",
        };
        db.Runbooks.Add(runbook);
        await db.SaveChangesAsync();

        var run = new RunbookRun
        {
            SpaceId = WellKnown.DefaultSpaceId, RunbookId = runbook.Id, ProjectId = project.Id,
            EnvironmentId = env.Id, Status = DeploymentStatus.Running, ProcessSnapshot = [],
            LeaseUntil = leaseMinutes is { } m ? DateTimeOffset.UtcNow.AddMinutes(m) : null,
            ClaimedBy = leaseMinutes is null ? null : "kraken:test",
        };
        db.RunbookRuns.Add(run);
        await db.SaveChangesAsync();

        db.TaskTargetAssignments.Add(new TaskTargetAssignment
        {
            TaskId = run.Id, TargetId = target.Id, AddedUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (run.Id, target.Id);
    }

    private static async Task SetRunningWithLeaseAsync(
        OrchestratorTestHarness harness, Guid taskId, int? leaseMinutes)
    {
        await using var db = harness.CreateContext();
        var until = leaseMinutes is { } m ? DateTimeOffset.UtcNow.AddMinutes(m) : (DateTimeOffset?)null;
        var owner = leaseMinutes is null ? null : "kraken:test";
        await db.ServerTasks.IgnoreQueryFilters()
            .Where(t => t.Id == taskId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, DeploymentStatus.Running)
                .SetProperty(t => t.LeaseUntil, until)
                .SetProperty(t => t.ClaimedBy, owner));
    }

    private static async Task<ServerTask> GetTaskAsync(OrchestratorTestHarness harness, Guid taskId)
    {
        await using var db = harness.CreateContext();
        return await db.ServerTasks.IgnoreQueryFilters().FirstAsync(t => t.Id == taskId);
    }

    // ── Hub construction (fresh, empty registry → the fallback path) ───────────

    private AgentHub BuildHub(Guid actingTargetId)
    {
        var publisher = new TargetStatusPublisher(
            new InMemoryTargetStatusNotifier(),
            new NullUiHubContext(),
            NullLogger<TargetStatusPublisher>.Instance);

        return new AgentHub(
            new InMemoryAgentConnectionRegistry(),
            postgres,
            postgres.ScopeFactory,
            publisher,
            TimeProvider.System,
            new NullUiHubContext(),
            new PendingSubPlanRegistry(),   // empty → RouteCompletion = NoPendingSubPlan
            new NeverUsedAdhocRegistry(),
            new KrakenDeploy.Server.Data.Accounts.DisabledAccountContext(),
            TestCrypto.Service(DevMasterKey),
            new NoopAuditLog(),
            NullLogger<AgentHub>.Instance)
        {
            Context = new FakeHubCallerContext(actingTargetId),
        };
    }
}

// ── Test doubles (file-scoped) ───────────────────────────────────────────────

file sealed class NoopAuditLog : KrakenDeploy.Server.Core.Domain.Audit.IAuditLog
{
    public Task RecordAsync(
        string eventType, string? subjectType = null, string? subjectId = null,
        string? subjectName = null, string? details = null, Guid? userId = null,
        string? userDisplay = null, CancellationToken ct = default) => Task.CompletedTask;
}

file sealed class FakeHubCallerContext(Guid targetId) : HubCallerContext
{
    private readonly ClaimsPrincipal _user = new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, targetId.ToString())]));

    public override string ConnectionId => "test-connection";
    public override string? UserIdentifier => targetId.ToString();
    public override ClaimsPrincipal? User => _user;
    public override System.Collections.Generic.IDictionary<object, object?> Items { get; }
        = new System.Collections.Generic.Dictionary<object, object?>();
    public override IFeatureCollection Features { get; } = new FeatureCollection();
    public override CancellationToken ConnectionAborted => CancellationToken.None;
    public override void Abort() { }
}

file sealed class NeverUsedAdhocRegistry : IPendingAdhocRegistry
{
    public void Register(Guid sessionId, int iterNumber, Guid targetId,
        TaskCompletionSource<AdhocScriptResult> tcs)
        => throw new NotSupportedException("IPendingAdhocRegistry is not used by these methods.");

    public bool TryResolve(Guid sessionId, int iterNumber, Guid targetId, AdhocScriptResult result)
        => throw new NotSupportedException("IPendingAdhocRegistry is not used by these methods.");

    public void Cancel(Guid sessionId, int iterNumber, Guid targetId, string reason)
        => throw new NotSupportedException("IPendingAdhocRegistry is not used by these methods.");
}
