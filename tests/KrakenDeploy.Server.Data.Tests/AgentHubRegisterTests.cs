using System.Security.Claims;
using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Transport;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Tests <see cref="AgentHub.RegisterAsync"/> by instantiating the hub directly
/// with a fake <see cref="HubCallerContext"/> and stub dependencies.
/// This validates the DB-update logic without requiring a full SignalR transport.
/// End-to-end hub connectivity is covered by the M1 smoke test.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public class AgentHubRegisterTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task RegisterAsync_writes_machine_info_to_the_database()
    {
        await using var db = postgres.CreateContext();

        var target = new DeploymentTarget
        {
            Name = "hub-test-target",
            Roles = ["web"],
            TransportMode = TransportMode.Reverse,
        };
        db.DeploymentTargets.Add(target);
        await db.SaveChangesAsync();

        var hub = BuildHub(postgres, target.Id);
        await hub.RegisterAsync(new AgentRegistrationRequest(
            target.Id, "test-machine", "Linux 6.0", "1.0.0", 0L, 0L,
            AgentContract.CurrentVersion));

        await db.Entry(target).ReloadAsync();
        target.MachineName.Should().Be("test-machine");
        target.OperatingSystem.Should().Be("Linux 6.0");
        target.AgentVersion.Should().Be("1.0.0");
        target.Status.Should().Be(TargetStatus.Online);
    }

    [Fact]
    public async Task RegisterAsync_preserves_wizard_roles_when_agent_sends_empty_list()
    {
        await using var db = postgres.CreateContext();

        var target = new DeploymentTarget
        {
            Name = "hub-test-roles-preserve",
            Roles = ["wizard-role"],
            TransportMode = TransportMode.Reverse,
        };
        db.DeploymentTargets.Add(target);
        await db.SaveChangesAsync();

        // B6: the Roles wire field was REMOVED outright (T1-7's end state) —
        // self-declaration is unrepresentable, so "registration must not touch
        // server-side roles" is the property left to pin.
        await BuildHub(postgres, target.Id).RegisterAsync(
            new AgentRegistrationRequest(target.Id, "m", "o", "v", 0L, 0L,
                AgentContract.CurrentVersion));

        await db.Entry(target).ReloadAsync();
        string[] expectedRoles = ["wizard-role"];
        target.Roles.Should().Equal(expectedRoles,
            because: "registration carries no roles at all and must not overwrite server-configured ones");
    }

    [Fact]
    public async Task RegisterAsync_does_not_re_check_the_contract_version()
    {
        // The contract check does NOT live here any more — it moved onto the SignalR
        // handshake, where AgentContractHandshakeGate refuses a skew with 426 before the
        // connection is admitted. This test pins that the hub does not re-check it: a
        // second, later gate would reintroduce the connected-but-unverified window whose
        // removal is the whole point of the move, and it would fail closed on a connection
        // the handshake had already cleared.
        //
        // The skew itself is refused end to end by
        // MultiAccountAgentTransportE2ETests.Agent_with_a_skewed_contract_version_is_refused,
        // which drives a real SignalR client through the real middleware. That is the only
        // honest place to assert it: a hub method never sees the handshake, so a unit test
        // here could only ever re-assert a check that should no longer exist.
        await using var db = postgres.CreateContext();

        var target = new DeploymentTarget
        {
            Name = "hub-test-contract-not-rechecked",
            Roles = ["web"],
            TransportMode = TransportMode.Reverse,
            Status = TargetStatus.Online,
        };
        db.DeploymentTargets.Add(target);
        await db.SaveChangesAsync();

        var registry = new InMemoryAgentConnectionRegistry();
        registry.Add("test-connection", target.Id);

        var previousVersion = AgentContract.CurrentVersion - 1;
        var result = await BuildHub(postgres, target.Id, registry).RegisterAsync(
            new AgentRegistrationRequest(target.Id, "m", "o", "f2-era", 0L, 0L,
                ContractVersion: previousVersion));

        result.Accepted.Should().BeTrue(
            "the hub trusts the handshake gate and must not gate on the version a second time");
        registry.GetConnectionId(target.Id).Should().Be("test-connection",
            "a tracked connection is dispatchable — there is no separate registration step");

        await db.Entry(target).ReloadAsync();
        target.Status.Should().Be(TargetStatus.Online);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static AgentHub BuildHub(
        PostgresFixture postgres, Guid targetId, IAgentConnectionRegistry? registry = null)
    {
        var publisher = new TargetStatusPublisher(
            new InMemoryTargetStatusNotifier(),
            new NullUiHubContext(),
            NullLogger<TargetStatusPublisher>.Instance);

        var hub = new AgentHub(
            registry ?? new InMemoryAgentConnectionRegistry(),
            postgres,
            new NeverUsedScopeFactory(),
            publisher,
            TimeProvider.System,
            new NullUiHubContext(),
            new NeverUsedPendingSubPlanRegistry(),
            new NeverUsedPendingAdhocRegistry(),
            new KrakenDeploy.Server.Data.Accounts.DisabledAccountContext(),
            TestCrypto.Service("S3Jha2VuRGVwbG95RGV2TWFzdGVyS2V5MzJCeXRlcyE="),
            new RecordingAuditLog(postgres),
            NullLogger<AgentHub>.Instance);

        hub.Context = new FakeHubCallerContext(targetId);
        return hub;
    }

    // Writes audit rows to the test DB so the role-rejection assertion can read them.
    private sealed class RecordingAuditLog(PostgresFixture postgres) : IAuditLog
    {
        public async Task RecordAsync(
            string eventType,
            string? subjectType = null,
            string? subjectId = null,
            string? subjectName = null,
            string? details = null,
            Guid? userId = null,
            string? userDisplay = null,
            CancellationToken ct = default)
        {
            await using var db = postgres.CreateContext();
            db.AuditEntries.Add(new AuditEntry
            {
                EventType   = eventType,
                SubjectType = subjectType,
                SubjectId   = subjectId,
                SubjectName = subjectName,
                Details     = details,
                OccurredUtc = DateTimeOffset.UtcNow,
                SpaceId     = WellKnown.DefaultSpaceId,
                UserId      = userId,
                UserDisplay = userDisplay ?? "test",
            });
            await db.SaveChangesAsync(ct);
        }
    }
}

// ── Test doubles ────────────────────────────────────────────────────────────

file sealed class FakeHubCallerContext(Guid targetId) : HubCallerContext
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

file sealed class NeverUsedScopeFactory : IServiceScopeFactory
{
    public IServiceScope CreateScope()
        => throw new NotSupportedException("IServiceScopeFactory is not used by RegisterAsync.");
}

file sealed class NeverUsedPendingSubPlanRegistry : IPendingSubPlanRegistry
{
    public void Register(Guid deploymentId, Guid targetId, Guid dispatchId,
        TaskCompletionSource<SubPlanResult> tcs, Action? onExecutionStarted = null)
        => throw new NotSupportedException("IPendingSubPlanRegistry is not used by RegisterAsync.");

    public bool TryMarkExecutionStarted(Guid deploymentId, Guid targetId, Guid dispatchId)
        => throw new NotSupportedException("IPendingSubPlanRegistry is not used by RegisterAsync.");

    public SubPlanCompletionRoute RouteCompletion(Guid deploymentId, Guid targetId, Guid dispatchId, SubPlanResult result)
        => throw new NotSupportedException("IPendingSubPlanRegistry is not used by RegisterAsync.");

    public void Cancel(Guid deploymentId, Guid targetId, string reason)
        => throw new NotSupportedException("IPendingSubPlanRegistry is not used by RegisterAsync.");

    public void RecordStepResult(Guid deploymentId, Guid targetId, Guid dispatchId, SubPlanStepResult result)
        => throw new NotSupportedException("IPendingSubPlanRegistry is not used by RegisterAsync.");

    public IReadOnlyList<SubPlanStepResult> DrainStepResults(Guid deploymentId, Guid targetId)
        => throw new NotSupportedException("IPendingSubPlanRegistry is not used by RegisterAsync.");

    public bool IsRetiredDispatch(Guid dispatchId)
        => throw new NotSupportedException("IPendingSubPlanRegistry is not used by RegisterAsync.");

    public bool HasSlot(Guid deploymentId, Guid targetId)
        => throw new NotSupportedException("IPendingSubPlanRegistry is not used by RegisterAsync.");
}

file sealed class NeverUsedPendingAdhocRegistry : IPendingAdhocRegistry
{
    public void Register(Guid sessionId, int iterNumber, Guid targetId,
        TaskCompletionSource<KrakenDeploy.Contracts.Adhoc.AdhocScriptResult> tcs)
        => throw new NotSupportedException("IPendingAdhocRegistry is not used by RegisterAsync.");

    public bool TryResolve(Guid sessionId, int iterNumber, Guid targetId,
        KrakenDeploy.Contracts.Adhoc.AdhocScriptResult result)
        => throw new NotSupportedException("IPendingAdhocRegistry is not used by RegisterAsync.");

    public void Cancel(Guid sessionId, int iterNumber, Guid targetId, string reason)
        => throw new NotSupportedException("IPendingAdhocRegistry is not used by RegisterAsync.");
}

file sealed class NullUiHubContext : IHubContext<UiHub, IUiHubClient>
{
    public IHubClients<IUiHubClient> Clients { get; } = new NullHubClients();
    public IGroupManager Groups => throw new NotSupportedException();

    private sealed class NullHubClients : IHubClients<IUiHubClient>
    {
        // Instance field — prevents CA1822 on all members that return it.
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
            string level, string message)
            => Task.CompletedTask;

        public Task DeploymentStatusChangedAsync(Guid deploymentId, string status)
            => Task.CompletedTask;
    }
}
