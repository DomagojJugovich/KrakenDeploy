using System.Collections.Concurrent;
using System.Security.Claims;
using System.Threading.Channels;
using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Runbooks;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;
using KrakenDeploy.Server.Transport;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Gap 2 (agent trust boundary) regression coverage. The control-plane hub
/// methods (<see cref="AgentHub.CompleteDeploymentAsync"/>,
/// <see cref="AgentHub.AppendLogAsync"/>, <see cref="AgentHub.ReportStepCompletedAsync"/>)
/// resolve a deployment purely by the wire-supplied id. Without an ownership
/// gate, an agent authenticated as target <c>foreign</c> can complete / log /
/// inject output variables into a deployment that belongs to other targets.
/// <para>
/// The check must key on the deployment's target SET (the
/// <c>deployment_target_assignments</c> join), NOT the legacy single
/// <c>Deployment.TargetId</c> — otherwise every non-primary target in a
/// multi-target wave (the <c>secondary</c> cases below) would be wrongly denied.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class AgentHubOwnershipTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    // 32-byte base64 DEK — same key BuildHub passes to the hub's encryption
    // service, so the test can decrypt what the hub stored.
    private const string DevMasterKey = "S3Jha2VuRGVwbG95RGV2TWFzdGVyS2V5MzJCeXRlcyE=";

    // ── CompleteDeploymentAsync ──────────────────────────────────────────────

    // NOTE (E1 → D1 Phase 3): the hub NEVER finalizes a task — a completion
    // with no open orchestrator slot is dropped for either kind (the legacy
    // runbook hand-off finalize was deleted with reconciler arm 4; the drop
    // behaviour is covered by AgentHubFallbackFinalizeTests). The two tests
    // below stay as tripwires: whatever the ownership verdict, no DB write may
    // happen on this path. The ownership PREDICATE itself is kind-agnostic and
    // covered with observable effects by
    // AgentDeploymentOwnership_matches_only_assigned_targets + the AppendLog /
    // ReportStepCompleted cases below, which use deployments.

    [Fact]
    public async Task CompleteDeploymentAsync_rejects_unassigned_target()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var g = await SeedRunbookGraphAsync(harness);

        // foreign target is NOT in the run's target set.
        await BuildHub(postgres, g.Foreign.Id)
            .CompleteDeploymentAsync(g.RunId, Guid.Empty, success: true, errorMessage: null);

        var run = await GetTaskAsync(harness, g.RunId);
        run.Status.Should().Be(DeploymentStatus.Running,
            "an agent not assigned to the run must not be able to complete it");
        run.CompletedUtc.Should().BeNull();
    }

    [Fact]
    public async Task CompleteDeploymentAsync_never_overwrites_a_cancelled_task()
    {
        // B5 (T1-1) tripwire: a late agent completion — delivered by B2's
        // at-least-once outbox after an operator cancel, or after the reconciler
        // already failed the task — must not flip the recorded verdict back to
        // Succeeded, nor re-stamp CompletedUtc. Post-Phase-3 the hub has no
        // finalize write at all, so this holds by construction; the test stays
        // so any reintroduced hub write trips it immediately.
        await using var harness = new OrchestratorTestHarness(postgres);
        var g = await SeedRunbookGraphAsync(harness);

        var cancelStamp = DateTimeOffset.UtcNow;
        await using (var db = harness.CreateContext())
        {
            await db.ServerTasks.IgnoreQueryFilters()
                .Where(t => t.Id == g.RunId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status, DeploymentStatus.Cancelled)
                    .SetProperty(t => t.CompletedUtc, cancelStamp));
        }

        // From an ASSIGNED target — passes the ownership gate; the hub must
        // still write nothing.
        await BuildHub(postgres, g.Primary.Id)
            .CompleteDeploymentAsync(g.RunId, Guid.Empty, success: true, errorMessage: null);

        var run = await GetTaskAsync(harness, g.RunId);
        run.Status.Should().Be(DeploymentStatus.Cancelled,
            "the operator's cancel is the recorded verdict — a late agent success must not flip it");
        run.CompletedUtc.Should().NotBeNull();
        run.CompletedUtc!.Value.Should().BeCloseTo(cancelStamp, TimeSpan.FromMilliseconds(1));
    }

    // ── AppendLogAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task AppendLogAsync_rejects_unassigned_target()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var g = await SeedAsync(harness);

        await BuildHub(postgres, g.Foreign.Id)
            .AppendLogAsync(g.DeploymentId, Guid.Empty, 0, "Information", "injected");

        await using var db = harness.CreateContext();
        (await db.TaskLogLive.IgnoreQueryFilters()
            .CountAsync(e => e.TaskId == g.DeploymentId))
            .Should().Be(0, "a foreign agent must not inject log lines into another target's deployment");
    }

    [Fact]
    public async Task AppendLogAsync_allows_assigned_target()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var g = await SeedAsync(harness);

        // secondary (join-only) must be allowed — all wave targets log against
        // the same deployment id.
        await BuildHub(postgres, g.Secondary.Id)
            .AppendLogAsync(g.DeploymentId, Guid.Empty, 0, "Information", "legit");

        await using var db = harness.CreateContext();
        (await db.TaskLogLive.IgnoreQueryFilters()
            .CountAsync(e => e.TaskId == g.DeploymentId))
            .Should().Be(1);
    }

    [Fact]
    public async Task AppendLogAsync_drops_lines_from_a_retired_dispatch()
    {
        // B6: a superseded/timed-out attempt's outbox keeps flushing after the
        // wave was re-dispatched — its lines must not interleave into the
        // current attempt's log. Only POSITIVE retirement drops a line:
        // Guid.Empty and unknown dispatch ids are always accepted.
        await using var harness = new OrchestratorTestHarness(postgres);
        var g = await SeedAsync(harness);

        var registry = new PendingSubPlanRegistry();
        var retiredDispatch = Guid.NewGuid();
        registry.Register(g.DeploymentId, g.Primary.Id, retiredDispatch,
            new TaskCompletionSource<SubPlanResult>());
        registry.Cancel(g.DeploymentId, g.Primary.Id, "wave re-dispatched");

        var hub = BuildHub(postgres, g.Primary.Id, subPlans: registry);
        await hub.AppendLogAsync(g.DeploymentId, retiredDispatch, 0, "info", "stale attempt noise");
        await hub.AppendLogAsync(g.DeploymentId, Guid.Empty, 0, "info", "legacy line");
        await hub.AppendLogAsync(g.DeploymentId, Guid.NewGuid(), 0, "info", "unknown dispatch line");

        await using var db = harness.CreateContext();
        var messages = await db.TaskLogLive.IgnoreQueryFilters()
            .Where(e => e.TaskId == g.DeploymentId)
            .Select(e => e.Message)
            .ToListAsync();
        messages.Should().BeEquivalentTo(["legacy line", "unknown dispatch line"],
            "only the positively-retired dispatch's line is dropped");
    }

    [Fact]
    public async Task ReportStepCompletedAsync_persists_nothing_for_a_retired_dispatch()
    {
        // E-C: a retired attempt's late step report — flushed from the B2 outbox
        // after the wave was superseded/re-dispatched — must not touch the DB
        // half. RecordStepResult already self-guards in memory, but the DB
        // persistence is dispatch-agnostic: without the mirrored guard the upsert
        // (keyed (task, step, name), no dispatch dimension) OVERWRITES the CURRENT
        // attempt's outputs and CompactStepAsync prematurely folds the current
        // attempt's staged step lines. Register attempt B, retire attempt A,
        // replay A: A's outputs stay absent and B's staged lines stay uncompacted.
        await using var harness = new OrchestratorTestHarness(postgres);
        var g = await SeedAsync(harness);

        var registry = new PendingSubPlanRegistry();
        var retiredDispatch = Guid.NewGuid();   // attempt A
        var currentDispatch = Guid.NewGuid();   // attempt B
        // Attempt A registered then cancelled → its dispatch id is positively retired.
        registry.Register(g.DeploymentId, g.Primary.Id, retiredDispatch,
            new TaskCompletionSource<SubPlanResult>());
        registry.Cancel(g.DeploymentId, g.Primary.Id, "wave superseded");
        // Attempt B is the CURRENT in-flight wave.
        registry.Register(g.DeploymentId, g.Primary.Id, currentDispatch,
            new TaskCompletionSource<SubPlanResult>());

        var hub = BuildHub(postgres, g.Primary.Id, subPlans: registry);

        // Attempt B stages a live log line for step 0 (not yet compacted).
        await hub.AppendLogAsync(g.DeploymentId, currentDispatch, 0, "Information", "B step-0 line");

        // Replay attempt A's step completion for the SAME step 0, carrying outputs.
        await hub.ReportStepCompletedAsync(
            g.DeploymentId, retiredDispatch, stepIndex: 0, stepName: "Deploy",
            success: true, errorMessage: null,
            outputVariables: new Dictionary<string, string> { ["Injected"] = "stale" },
            sensitiveOutputNames: []);

        await using var db = harness.CreateContext();

        (await db.TaskOutputVariables.IgnoreQueryFilters()
            .CountAsync(o => o.TaskId == g.DeploymentId))
            .Should().Be(0, "a retired attempt's late report must persist no output variables");

        (await db.TaskLogLive.IgnoreQueryFilters()
            .CountAsync(e => e.TaskId == g.DeploymentId))
            .Should().Be(1, "the current attempt's staged line must survive — not be folded by a retired report");

        (await db.TaskStepLogs.IgnoreQueryFilters()
            .CountAsync(b => b.TaskId == g.DeploymentId))
            .Should().Be(0, "the retired report must not prematurely compact the current attempt's step");
    }

    [Fact]
    public async Task ReportStepCompletedAsync_rejects_a_negative_step_index()
    {
        // B6 trust boundary: the step index is agent-supplied and downstream
        // resolves it against the plan's snapshot array — a malformed value
        // must die here, not abort the deployment inside the wave fold.
        await using var harness = new OrchestratorTestHarness(postgres);
        var g = await SeedAsync(harness);

        await BuildHub(postgres, g.Primary.Id).ReportStepCompletedAsync(
            g.DeploymentId, Guid.Empty, stepIndex: -7, stepName: "Deploy",
            success: true, errorMessage: null,
            outputVariables: new Dictionary<string, string> { ["X"] = "1" },
            sensitiveOutputNames: []);

        await using var db = harness.CreateContext();
        (await db.TaskOutputVariables.IgnoreQueryFilters()
            .CountAsync(v => v.TaskId == g.DeploymentId))
            .Should().Be(0, "a rejected report must persist nothing");
    }

    // ── ReportStepCompletedAsync (output-variable injection) ─────────────────

    [Fact]
    public async Task ReportStepCompletedAsync_rejects_unassigned_target()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var g = await SeedAsync(harness);

        await BuildHub(postgres, g.Foreign.Id).ReportStepCompletedAsync(
            g.DeploymentId, Guid.Empty, 0, "Deploy", success: true, errorMessage: null,
            outputVariables: new Dictionary<string, string> { ["Injected"] = "evil" },
            sensitiveOutputNames: []);

        await using var db = harness.CreateContext();
        (await db.TaskOutputVariables.IgnoreQueryFilters()
            .CountAsync(o => o.TaskId == g.DeploymentId))
            .Should().Be(0, "a foreign agent must not inject output variables that later steps consume");
    }

    [Fact]
    public async Task ReportStepCompletedAsync_allows_assigned_target()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var g = await SeedAsync(harness);

        await BuildHub(postgres, g.Primary.Id).ReportStepCompletedAsync(
            g.DeploymentId, Guid.Empty, 0, "Deploy", success: true, errorMessage: null,
            outputVariables: new Dictionary<string, string> { ["Url"] = "https://x" },
            sensitiveOutputNames: []);

        await using var db = harness.CreateContext();
        (await db.TaskOutputVariables.IgnoreQueryFilters()
            .CountAsync(o => o.TaskId == g.DeploymentId))
            .Should().Be(1);
    }

    // ── T0-6: sensitive output variables encrypted at rest + masked in UI ────

    [Fact]
    public async Task ReportStepCompletedAsync_encrypts_sensitive_output_and_leaves_plaintext_alone()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var g = await SeedAsync(harness);

        const string secret = "p@ss w0rd with spaces";
        const string plain  = "https://public.example";

        await BuildHub(postgres, g.Primary.Id).ReportStepCompletedAsync(
            g.DeploymentId, Guid.Empty, 0, "Deploy", success: true, errorMessage: null,
            outputVariables: new Dictionary<string, string>
            {
                ["Token"] = secret,
                ["Url"]   = plain,
            },
            sensitiveOutputNames: ["Token"]);

        await using var db = harness.CreateContext();
        var rows = await db.TaskOutputVariables.IgnoreQueryFilters()
            .Where(o => o.TaskId == g.DeploymentId)
            .ToDictionaryAsync(o => o.Name);

        // Sensitive row: flagged, NOT stored plaintext, and decrypts back.
        rows["Token"].IsSensitive.Should().BeTrue();
        rows["Token"].Value.Should().NotBe(secret, "the sensitive value must be encrypted at rest");
        var crypto = TestCrypto.Service(DevMasterKey);
        crypto.Decrypt(rows["Token"].Value).Should().Be(secret);

        // Non-sensitive row: untouched.
        rows["Url"].IsSensitive.Should().BeFalse();
        rows["Url"].Value.Should().Be(plain);

        // Read path masks the sensitive value to *** and never exposes ciphertext.
        var queue = Channel.CreateUnbounded<KrakenDeploy.Server.Data.TenantWorkItem>();
        var service = new KrakenDeploy.Server.Data.Services.DeploymentService(
            postgres, queue, TimeProvider.System,
            new KrakenDeploy.Server.Data.Accounts.DisabledAccountContext(),
            new AllowAllPermissionEvaluator());
        var display = (await service.GetOutputVariablesAsync(g.DeploymentId))
            .ToDictionary(o => o.Name);
        display["Token"].Value.Should().Be("***");
        display["Url"].Value.Should().Be(plain);
    }

    // ── Shared ownership predicate (also gates the gRPC artifact upload) ─────

    [Fact]
    public async Task AgentDeploymentOwnership_matches_only_assigned_targets()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var g = await SeedAsync(harness);
        await using var db = harness.CreateContext();

        (await AgentDeploymentOwnership.ConnectionOwnsTaskAsync(db, g.DeploymentId, g.Foreign.Id))
            .Should().BeFalse("an unassigned target does not own the deployment");
        (await AgentDeploymentOwnership.ConnectionOwnsTaskAsync(db, g.DeploymentId, g.Primary.Id))
            .Should().BeTrue("the legacy primary target owns the deployment");
        (await AgentDeploymentOwnership.ConnectionOwnsTaskAsync(db, g.DeploymentId, g.Secondary.Id))
            .Should().BeTrue("a join-set (secondary wave) target owns the deployment");
        (await AgentDeploymentOwnership.ConnectionOwnsTaskAsync(db, Guid.NewGuid(), g.Primary.Id))
            .Should().BeFalse("an unknown deployment id is owned by no one");
    }

    // ── E7: reconnect cancel-reconcile ───────────────────────────────────────

    [Fact]
    public async Task Reconcile_repushes_cancel_for_a_recent_cancelled_task()
    {
        // The agent was offline when the operator cancelled, so the original push
        // skipped it (no live connection). On reconnect the hub must re-push a
        // cooperative cancel — straight to this connection — for the recently
        // terminal task it may still be running.
        await using var harness = new OrchestratorTestHarness(postgres);
        var g = await SeedAsync(harness);
        await SetTerminalAsync(harness, g.DeploymentId, DeploymentStatus.Cancelled, DateTimeOffset.UtcNow);

        var (scope, agent) = PushViaFakeHub(g.Primary.Id, "conn-primary");
        await BuildHub(postgres, g.Primary.Id, scopeFactory: scope)
            .ReconcileTerminalTasksForReconnectAsync(g.Primary.Id, "conn-primary");

        agent.CancelPushes.Select(p => p.TaskId).Should().ContainSingle()
            .Which.Should().Be(g.DeploymentId);
    }

    [Fact]
    public async Task Reconcile_repushes_cancel_for_a_recent_failed_task()
    {
        // A reconciler-interrupted (lease-expired → Failed) task the agent may
        // still be running is reconciled the same way as an operator cancel.
        await using var harness = new OrchestratorTestHarness(postgres);
        var g = await SeedAsync(harness);
        await SetTerminalAsync(harness, g.DeploymentId, DeploymentStatus.Failed, DateTimeOffset.UtcNow);

        var (scope, agent) = PushViaFakeHub(g.Primary.Id, "conn-primary");
        await BuildHub(postgres, g.Primary.Id, scopeFactory: scope)
            .ReconcileTerminalTasksForReconnectAsync(g.Primary.Id, "conn-primary");

        agent.CancelPushes.Select(p => p.TaskId).Should().Contain(g.DeploymentId);
    }

    [Fact]
    public async Task Reconcile_skips_succeeded_tasks()
    {
        // A task that completed successfully means the agent already reported
        // back — it is not still running, so nothing is pushed.
        await using var harness = new OrchestratorTestHarness(postgres);
        var g = await SeedAsync(harness);
        await SetTerminalAsync(harness, g.DeploymentId, DeploymentStatus.Succeeded, DateTimeOffset.UtcNow);

        var (scope, agent) = PushViaFakeHub(g.Primary.Id, "conn-primary");
        await BuildHub(postgres, g.Primary.Id, scopeFactory: scope)
            .ReconcileTerminalTasksForReconnectAsync(g.Primary.Id, "conn-primary");

        agent.CancelPushes.Should().BeEmpty();
    }

    [Fact]
    public async Task Reconcile_skips_tasks_terminal_outside_the_window()
    {
        // A task that went terminal long ago is assumed no longer executing.
        await using var harness = new OrchestratorTestHarness(postgres);
        var g = await SeedAsync(harness);
        await SetTerminalAsync(harness, g.DeploymentId, DeploymentStatus.Cancelled,
            DateTimeOffset.UtcNow - TimeSpan.FromHours(6));

        var (scope, agent) = PushViaFakeHub(g.Primary.Id, "conn-primary");
        await BuildHub(postgres, g.Primary.Id, scopeFactory: scope)
            .ReconcileTerminalTasksForReconnectAsync(g.Primary.Id, "conn-primary");

        agent.CancelPushes.Should().BeEmpty();
    }

    [Fact]
    public async Task Reconcile_skips_tasks_not_assigned_to_the_target()
    {
        // foreign is not in the deployment's target set — its reconnect must not
        // re-cancel another target's task.
        await using var harness = new OrchestratorTestHarness(postgres);
        var g = await SeedAsync(harness);
        await SetTerminalAsync(harness, g.DeploymentId, DeploymentStatus.Cancelled, DateTimeOffset.UtcNow);

        var (scope, agent) = PushViaFakeHub(g.Foreign.Id, "conn-foreign");
        await BuildHub(postgres, g.Foreign.Id, scopeFactory: scope)
            .ReconcileTerminalTasksForReconnectAsync(g.Foreign.Id, "conn-foreign");

        agent.CancelPushes.Should().BeEmpty();
    }

    [Fact]
    public async Task RegisterAsync_repushes_cancel_on_reconnect()
    {
        // Full path: registration (the agent's every-reconnect signal) drives the
        // reconcile inline, so completion is deterministic. The hub's fake caller
        // context connection id is "test-connection".
        await using var harness = new OrchestratorTestHarness(postgres);
        var g = await SeedAsync(harness);
        await SetTerminalAsync(harness, g.DeploymentId, DeploymentStatus.Cancelled, DateTimeOffset.UtcNow);

        var (scope, agent) = PushViaFakeHub(g.Primary.Id, "test-connection");
        var result = await BuildHub(postgres, g.Primary.Id, scopeFactory: scope)
            .RegisterAsync(new AgentRegistrationRequest(
                g.Primary.Id, "m", "o", "1.0.0", 0L, 0L, AgentContract.CurrentVersion));

        result.Accepted.Should().BeTrue();
        agent.CancelPushes.Select(p => p.TaskId).Should().Contain(g.DeploymentId,
            "registration on reconnect must reconcile in-flight cancellations");
    }

    /// <summary>Builds a scope factory whose <c>IHubContext&lt;AgentHub&gt;</c> is a
    /// <see cref="FakeAgentHubContext"/> routing <paramref name="connectionId"/> to a
    /// fresh <see cref="FakeAgent"/> (which records the cancel pushes it receives).</summary>
    private static (IServiceScopeFactory Scope, FakeAgent Agent) PushViaFakeHub(
        Guid targetId, string connectionId)
    {
        var registry = new InMemoryAgentConnectionRegistry();
        registry.Add(connectionId, targetId);
        var agent = new FakeAgent { TargetId = targetId, ConnectionId = connectionId };
        var agents = new ConcurrentDictionary<Guid, FakeAgent>();
        agents[targetId] = agent;

        var services = new ServiceCollection();
        services.AddSingleton<IHubContext<AgentHub, IAgentHubClient>>(
            new FakeAgentHubContext(new PendingSubPlanRegistry(), registry, agents));
        return (services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), agent);
    }

    private static async Task SetTerminalAsync(
        OrchestratorTestHarness harness, Guid taskId,
        DeploymentStatus status, DateTimeOffset completedUtc)
    {
        await using var db = harness.CreateContext();
        await db.ServerTasks.IgnoreQueryFilters()
            .Where(t => t.Id == taskId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, status)
                .SetProperty(t => t.CompletedUtc, completedUtc));
    }

    // ── Seeding + hub construction ───────────────────────────────────────────

    private sealed record Graph(
        Guid DeploymentId,
        DeploymentTarget Primary,
        DeploymentTarget Secondary,
        DeploymentTarget Foreign);

    private static async Task<Graph> SeedAsync(OrchestratorTestHarness harness)
    {
        // Unique names per call — all tests in this class share one Postgres
        // fixture, so fixed slugs/names would collide on the unique indexes.
        var tag = Guid.NewGuid().ToString("N")[..8];
        var project = await harness.SeedProjectAsync($"own-proj-{tag}");
        var env = await harness.SeedEnvironmentAsync($"own-env-{tag}");
        var members = await harness.SeedTargetsAsync($"owner-{tag}", $"secondary-{tag}");
        var foreign = (await harness.SeedTargetsAsync($"foreign-{tag}"))[0];
        var release = await harness.SeedReleaseAsync(project.Id, "1.0.0");
        // CreateDeploymentAsync sets TargetId = members[0] (legacy) and adds a
        // DeploymentTargetAssignment row for every member.
        var deploymentId = await harness.CreateDeploymentAsync(release.Id, env.Id, members);
        return new Graph(deploymentId, members[0], members[1], foreign);
    }

    private sealed record RunbookGraph(
        Guid RunId,
        DeploymentTarget Primary,
        DeploymentTarget Foreign);

    // A Running runbook run assigned to one target, with a second unassigned
    // (foreign). No lease, no registered sub-plan slot → a completion reaches the
    // hub's post-registry drop path, where the ownership gate (and nothing else)
    // runs. (The join-only-secondary "allows" case the second assignment used to
    // back was removed with the pre-Phase-3 finalize tests; the ownership
    // predicate's multi-target behaviour is covered by the deployment SeedAsync.)
    private static async Task<RunbookGraph> SeedRunbookGraphAsync(OrchestratorTestHarness harness)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var project = await harness.SeedProjectAsync($"own-rb-proj-{tag}");
        var env = await harness.SeedEnvironmentAsync($"own-rb-env-{tag}");
        var owner = (await harness.SeedTargetsAsync($"rb-owner-{tag}"))[0];
        var foreign = (await harness.SeedTargetsAsync($"rb-foreign-{tag}"))[0];

        await using var db = harness.CreateContext();
        var runbook = new Runbook
        {
            SpaceId = WellKnown.DefaultSpaceId, ProjectId = project.Id, Name = $"own-rb-{tag}",
        };
        db.Runbooks.Add(runbook);
        await db.SaveChangesAsync();

        var run = new RunbookRun
        {
            SpaceId = WellKnown.DefaultSpaceId, RunbookId = runbook.Id, ProjectId = project.Id,
            EnvironmentId = env.Id, Status = DeploymentStatus.Running, ProcessSnapshot = [],
        };
        db.RunbookRuns.Add(run);
        await db.SaveChangesAsync();

        db.TaskTargetAssignments.Add(new TaskTargetAssignment
        {
            TaskId = run.Id, TargetId = owner.Id, AddedUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return new RunbookGraph(run.Id, owner, foreign);
    }

    private static async Task<ServerTask> GetTaskAsync(OrchestratorTestHarness harness, Guid taskId)
    {
        await using var db = harness.CreateContext();
        return await db.ServerTasks.IgnoreQueryFilters().FirstAsync(t => t.Id == taskId);
    }

    private static AgentHub BuildHub(
        PostgresFixture postgres, Guid actingTargetId, IPendingSubPlanRegistry? subPlans = null,
        IServiceScopeFactory? scopeFactory = null)
    {
        var publisher = new TargetStatusPublisher(
            new InMemoryTargetStatusNotifier(),
            new OwnershipNullUiHubContext(),
            NullLogger<TargetStatusPublisher>.Instance);

        var hub = new AgentHub(
            new InMemoryAgentConnectionRegistry(),
            postgres,
            scopeFactory ?? postgres.ScopeFactory,
            publisher,
            TimeProvider.System,
            new OwnershipNullUiHubContext(),
            subPlans ?? new FalseSubPlanRegistry(),
            new OwnershipNeverUsedAdhocRegistry(),
            new KrakenDeploy.Server.Data.Accounts.DisabledAccountContext(),
            TestCrypto.Service(DevMasterKey),
            new OwnershipNoopAuditLog(),
            NullLogger<AgentHub>.Instance)
        {
            Context = new OwnershipFakeHubCallerContext(actingTargetId),
        };
        return hub;
    }
}

// ── Test doubles (file-scoped) ───────────────────────────────────────────────

file sealed class OwnershipNoopAuditLog : KrakenDeploy.Server.Core.Domain.Audit.IAuditLog
{
    public Task RecordAsync(
        string eventType, string? subjectType = null, string? subjectId = null,
        string? subjectName = null, string? details = null, Guid? userId = null,
        string? userDisplay = null, CancellationToken ct = default) => Task.CompletedTask;
}

file sealed class OwnershipFakeHubCallerContext(Guid targetId) : HubCallerContext
{
    private readonly ClaimsPrincipal _user = new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, targetId.ToString())]));

    public override string ConnectionId => "test-connection";
    public override string? UserIdentifier => targetId.ToString();
    public override ClaimsPrincipal? User => _user;
    public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
    public override IFeatureCollection Features { get; } = new FeatureCollection();
    public override CancellationToken ConnectionAborted => CancellationToken.None;
    public override void Abort() { }
}

/// <summary>Sub-plan registry whose RouteCompletion always reports no pending
/// sub-plan, forcing CompleteDeploymentAsync down the direct-write (legacy)
/// path where the ownership gate lives; all other members are harmless no-ops.</summary>
file sealed class FalseSubPlanRegistry : IPendingSubPlanRegistry
{
    public void Register(Guid deploymentId, Guid targetId, Guid dispatchId, TaskCompletionSource<SubPlanResult> tcs) { }
    public SubPlanCompletionRoute RouteCompletion(Guid deploymentId, Guid targetId, Guid dispatchId, SubPlanResult result)
        => SubPlanCompletionRoute.NoPendingSubPlan;
    public bool IsRetiredDispatch(Guid dispatchId) => false;
    public void Cancel(Guid deploymentId, Guid targetId, string reason) { }
    public void RecordStepResult(Guid deploymentId, Guid targetId, Guid dispatchId, SubPlanStepResult result) { }
    public IReadOnlyList<SubPlanStepResult> DrainStepResults(Guid deploymentId, Guid targetId)
        => [];
    public bool HasSlot(Guid deploymentId, Guid targetId) => false;
}

file sealed class OwnershipNeverUsedAdhocRegistry : IPendingAdhocRegistry
{
    public void Register(Guid sessionId, int iterNumber, Guid targetId,
        TaskCompletionSource<KrakenDeploy.Contracts.Adhoc.AdhocScriptResult> tcs)
        => throw new NotSupportedException("IPendingAdhocRegistry is not used by these methods.");

    public bool TryResolve(Guid sessionId, int iterNumber, Guid targetId,
        KrakenDeploy.Contracts.Adhoc.AdhocScriptResult result)
        => throw new NotSupportedException("IPendingAdhocRegistry is not used by these methods.");

    public void Cancel(Guid sessionId, int iterNumber, Guid targetId, string reason)
        => throw new NotSupportedException("IPendingAdhocRegistry is not used by these methods.");
}

file sealed class OwnershipNullUiHubContext : IHubContext<UiHub, IUiHubClient>
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
            Guid deploymentId, int sequence, DateTimeOffset timestamp, string level, string message)
            => Task.CompletedTask;
        public Task DeploymentStatusChangedAsync(Guid deploymentId, string status)
            => Task.CompletedTask;
    }
}
