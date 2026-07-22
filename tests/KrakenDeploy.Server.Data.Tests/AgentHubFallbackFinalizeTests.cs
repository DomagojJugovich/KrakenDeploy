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
/// E1 (execution-engine audit 2026-07-16) → D1 Phase 3 — the hub NEVER
/// finalizes a task. A completion <see cref="AgentHub.CompleteDeploymentAsync"/>
/// cannot route to an open sub-plan slot is DROPPED for either kind, whatever
/// the lease state.
/// <para>
/// The audited defect this guards against: a server restarts mid-task faster
/// than the 5-minute lease. The worker's in-memory wave state dies with the old
/// process; the boot reconciler defers to the still-live lease; the agent's
/// buffered WAVE completion flushes from its outbox into the FRESH process,
/// where <c>subPlans.RouteCompletion</c> finds no open slot
/// (<c>NoPendingSubPlan</c>); the pre-E1 fallback then wrote the WHOLE task
/// <c>Succeeded</c> although its remaining waves never ran. A single assigned
/// agent could flip a farm-wide verdict.
/// </para>
/// <para>
/// D1 Phase 3 deleted the last legitimate fallback user — the finalize for
/// LEGACY pre-D1 hand-off runbook runs (Running with a released lease) —
/// together with reconciler arm 4, so the post-registry path is now a pure
/// warn-and-drop. These tests drive the hub through it (a fresh, empty
/// <see cref="PendingSubPlanRegistry"/> routes every completion to
/// <c>NoPendingSubPlan</c>) and assert nothing is ever finalized.
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
    public async Task RunbookRun_completion_with_no_open_slot_is_dropped_even_without_a_lease()
    {
        // D1 Phase 3: the legacy hand-off finalize is deleted — a Running run
        // with a released lease is no longer a hand-off signature (that model is
        // gone); it is an ownerless orphan the RECONCILER fails. The hub must
        // not finalize it from one buffered wave completion.
        await using var harness = new OrchestratorTestHarness(postgres);
        var (runId, targetId) = await SeedRunningRunbookRunAsync(harness, leaseMinutes: null);

        await BuildHub(targetId)
            .CompleteDeploymentAsync(runId, Guid.NewGuid(), success: true, errorMessage: null);

        var run = await GetTaskAsync(harness, runId);
        run.Status.Should().Be(DeploymentStatus.Running,
            "the hub never finalizes a task post-Phase-3 — the null-lease orphan " +
            "verdict belongs to the dispatch reconciler");
        run.CompletedUtc.Should().BeNull();
    }

    [Fact]
    public async Task RunbookRun_completion_with_a_live_lease_is_dropped()
    {
        // A live lease means the run is worker-owned mid-orchestration — a lone
        // wave completion must not flip the whole run terminal.
        await using var harness = new OrchestratorTestHarness(postgres);
        var (runId, targetId) = await SeedRunningRunbookRunAsync(harness, leaseMinutes: 5);

        await BuildHub(targetId)
            .CompleteDeploymentAsync(runId, Guid.NewGuid(), success: true, errorMessage: null);

        var run = await GetTaskAsync(harness, runId);
        run.Status.Should().Be(DeploymentStatus.Running,
            "a live lease means the runbook run is worker-owned; the hub drops the completion");
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
