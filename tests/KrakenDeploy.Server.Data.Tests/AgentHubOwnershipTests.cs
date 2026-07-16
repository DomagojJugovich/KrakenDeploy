using System.Security.Claims;
using System.Threading.Channels;
using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Deployments;
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

    [Fact]
    public async Task CompleteDeploymentAsync_rejects_unassigned_target()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var g = await SeedAsync(harness);

        // foreign target is NOT in the deployment's target set.
        await BuildHub(postgres, g.Foreign.Id)
            .CompleteDeploymentAsync(g.DeploymentId, Guid.Empty, success: true, errorMessage: null);

        await using var db = harness.CreateContext();
        var dep = await db.Deployments.IgnoreQueryFilters()
            .FirstAsync(d => d.Id == g.DeploymentId);
        dep.Status.Should().Be(DeploymentStatus.Queued,
            "an agent not assigned to the deployment must not be able to complete it");
        dep.CompletedUtc.Should().BeNull();
    }

    [Fact]
    public async Task CompleteDeploymentAsync_allows_primary_target()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var g = await SeedAsync(harness);

        await BuildHub(postgres, g.Primary.Id)
            .CompleteDeploymentAsync(g.DeploymentId, Guid.Empty, success: false, errorMessage: "boom");

        await using var db = harness.CreateContext();
        var dep = await db.Deployments.IgnoreQueryFilters()
            .FirstAsync(d => d.Id == g.DeploymentId);
        dep.Status.Should().Be(DeploymentStatus.Failed);
    }

    [Fact]
    public async Task CompleteDeploymentAsync_allows_secondary_wave_target()
    {
        // secondary is in the join set but is NOT Deployment.TargetId — proves
        // the ownership predicate uses the assignment join, not the legacy column.
        await using var harness = new OrchestratorTestHarness(postgres);
        var g = await SeedAsync(harness);

        await BuildHub(postgres, g.Secondary.Id)
            .CompleteDeploymentAsync(g.DeploymentId, Guid.Empty, success: false, errorMessage: "boom");

        await using var db = harness.CreateContext();
        var dep = await db.Deployments.IgnoreQueryFilters()
            .FirstAsync(d => d.Id == g.DeploymentId);
        dep.Status.Should().Be(DeploymentStatus.Failed,
            "a non-primary target in the wave legitimately completes its sub-plan");
    }

    [Fact]
    public async Task CompleteDeploymentAsync_never_overwrites_a_cancelled_task()
    {
        // B5 (T1-1): the fallback write yields to a terminal verdict. A late
        // agent completion — delivered by B2's at-least-once outbox after an
        // operator cancel, or after the reconciler already failed the task —
        // must not flip the recorded verdict back to Succeeded, and must not
        // re-stamp CompletedUtc.
        await using var harness = new OrchestratorTestHarness(postgres);
        var g = await SeedAsync(harness);

        var cancelStamp = DateTimeOffset.UtcNow;
        await using (var db = harness.CreateContext())
        {
            await db.ServerTasks.IgnoreQueryFilters()
                .Where(t => t.Id == g.DeploymentId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status, DeploymentStatus.Cancelled)
                    .SetProperty(t => t.CompletedUtc, cancelStamp));
        }

        // From an ASSIGNED target — passes the ownership gate, reaches the
        // guarded fallback write.
        await BuildHub(postgres, g.Primary.Id)
            .CompleteDeploymentAsync(g.DeploymentId, Guid.Empty, success: true, errorMessage: null);

        await using var verify = harness.CreateContext();
        var dep = await verify.Deployments.IgnoreQueryFilters()
            .FirstAsync(d => d.Id == g.DeploymentId);
        dep.Status.Should().Be(DeploymentStatus.Cancelled,
            "the operator's cancel is the recorded verdict — a late agent success must not flip it");
        dep.CompletedUtc.Should().NotBeNull();
        dep.CompletedUtc!.Value.Should().BeCloseTo(cancelStamp, TimeSpan.FromMilliseconds(1));
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

    private static AgentHub BuildHub(
        PostgresFixture postgres, Guid actingTargetId, IPendingSubPlanRegistry? subPlans = null)
    {
        var publisher = new TargetStatusPublisher(
            new InMemoryTargetStatusNotifier(),
            new OwnershipNullUiHubContext(),
            NullLogger<TargetStatusPublisher>.Instance);

        var hub = new AgentHub(
            new InMemoryAgentConnectionRegistry(),
            postgres,
            postgres.ScopeFactory,
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
