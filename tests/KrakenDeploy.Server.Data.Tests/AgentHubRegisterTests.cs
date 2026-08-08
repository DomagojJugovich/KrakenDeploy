using System.Security.Claims;
using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;
using KrakenDeploy.Server.Transport;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    [Fact]
    public async Task OnConnectedAsync_leaves_no_registry_entry_when_a_write_throws()
    {
        // Round-5 finding 3. registry.Add used to run BEFORE the Online write and the status
        // push, both of which can throw — and SignalR deliberately skips OnDisconnectedAsync
        // after an OnConnectedAsync failure, so TryRemove never ran. The leaked entry is not
        // inert: it is exactly what makes a target dispatchable, so the wave sends to a dead
        // connection id (Clients.Client(deadId) is a silent no-op), HasConnectionFor reads true
        // so B3's disconnect monitor never diagnoses it, and the wave hangs to its deadline
        // while the fleet page shows the target green.
        //
        // The throwing seam is the in-process status notifier: TargetStatusPublisher catches
        // the UI-hub push but calls notifier.Publish OUTSIDE that try, because an in-process
        // subscriber failing is a bug rather than a transport blip.
        await using var db = postgres.CreateContext();
        var target = new DeploymentTarget
        {
            Name = "hub-test-connect-throws",
            Roles = ["web"],
            TransportMode = TransportMode.Reverse,
        };
        db.DeploymentTargets.Add(target);
        await db.SaveChangesAsync();

        var registry = new InMemoryAgentConnectionRegistry();
        var hub = BuildHub(postgres, target.Id, registry, notifier: new ThrowingStatusNotifier());

        var act = async () => await hub.OnConnectedAsync();

        await act.Should().ThrowAsync<InvalidOperationException>(
            "the failure must surface so SignalR closes the connection");
        registry.HasConnectionFor(target.Id).Should().BeFalse(
            "a connection whose OnConnectedAsync failed must not be left dispatchable — " +
            "nothing will ever remove it");
        registry.GetConnectionId(target.Id).Should().BeNull();
    }

    [Fact]
    public async Task RegisterAsync_re_pushes_cancels_even_when_the_machine_info_write_fails()
    {
        // Round-5 finding 6. The E7 reconcile used to run AFTER the machine-info
        // SaveChangesAsync, and this is its only call site: HeartbeatAsync repairs machine info
        // every 30 s but never re-invokes registration, and a healthy link produces no
        // reconnect. So a failed write skipped the reconcile with no retry path, and a task
        // cancelled while the agent was offline ran its step to completion on a production box
        // the operator had been told was cancelled.
        //
        // The write is made to fail for real rather than mocked: machine_name is
        // varchar(255), so an over-long value is a genuine Postgres error on save.
        await using var harness = new OrchestratorTestHarness(postgres);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var project = await harness.SeedProjectAsync($"reconcile-proj-{tag}");
        var env = await harness.SeedEnvironmentAsync($"reconcile-env-{tag}");
        var targets = await harness.SeedTargetsAsync($"reconcile-target-{tag}");
        var release = await harness.SeedReleaseAsync(project.Id, "1.0.0");
        var taskId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);

        // The task went terminal while the agent was away — the shape the E7 reconcile exists
        // for. A disconnect never aborts a running step, so the agent may still be executing it.
        await using (var db = harness.CreateContext())
        {
            var task = await db.ServerTasks.IgnoreQueryFilters().FirstAsync(t => t.Id == taskId);
            task.Status = DeploymentStatus.Cancelled;
            task.CompletedUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var cancels = new RecordingAgentHubContext();
        var hub = BuildHub(
            postgres, targets[0].Id, scopeFactory: new StubScopeFactory(cancels));

        var act = async () => await hub.RegisterAsync(new AgentRegistrationRequest(
            targets[0].Id, new string('m', 400), "Linux", "1.0.0", 0L, 0L,
            AgentContract.CurrentVersion));

        await act.Should().ThrowAsync<DbUpdateException>("the machine-info write must fail");
        cancels.Cancelled.Should().Contain(taskId,
            "the cooperative cancel must be re-pushed before anything that can fail — it is " +
            "the only thing that stops the step still running on the agent, and this is its " +
            "only call site");
    }

    [Fact]
    public async Task RegisterAsync_logs_an_error_when_the_body_contract_disagrees_with_the_gate()
    {
        // The tripwire for the one risk the handshake move introduced: enforcement rides a
        // request HEADER, so a header-whitelisting intermediary would strip it and the gate
        // would admit every agent silently. The body field is still on the wire, so a
        // disagreement means the header did not arrive as sent.
        await using var db = postgres.CreateContext();
        var target = new DeploymentTarget
        {
            Name = "hub-test-header-tripwire",
            Roles = ["web"],
            TransportMode = TransportMode.Reverse,
        };
        db.DeploymentTargets.Add(target);
        await db.SaveChangesAsync();

        var log = new ListLogger();
        await BuildHub(postgres, target.Id, logger: log).RegisterAsync(
            new AgentRegistrationRequest(target.Id, "m", "o", "v", 0L, 0L,
                ContractVersion: AgentContract.CurrentVersion - 1));

        log.Errors.Should().ContainSingle().Which.Should()
            .Contain(AgentContract.VersionHeader)
            .And.Contain("enforcing NOTHING");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static AgentHub BuildHub(
        PostgresFixture postgres,
        Guid targetId,
        IAgentConnectionRegistry? registry = null,
        ITargetStatusNotifier? notifier = null,
        IServiceScopeFactory? scopeFactory = null,
        ILogger<AgentHub>? logger = null)
    {
        var publisher = new TargetStatusPublisher(
            notifier ?? new InMemoryTargetStatusNotifier(),
            new NullUiHubContext(),
            NullLogger<TargetStatusPublisher>.Instance);

        var hub = new AgentHub(
            registry ?? new InMemoryAgentConnectionRegistry(),
            postgres,
            scopeFactory ?? new NeverUsedScopeFactory(),
            publisher,
            TimeProvider.System,
            new NullUiHubContext(),
            new NeverUsedPendingSubPlanRegistry(),
            new NeverUsedPendingAdhocRegistry(),
            new KrakenDeploy.Server.Data.Accounts.DisabledAccountContext(),
            TestCrypto.Service("S3Jha2VuRGVwbG95RGV2TWFzdGVyS2V5MzJCeXRlcyE="),
            new RecordingAuditLog(postgres),
            logger ?? NullLogger<AgentHub>.Instance);

        hub.Context = new FakeHubCallerContext(targetId);
        return hub;
    }

    /// <summary>An in-process status subscriber that faults — the reachable way to make
    /// <c>OnConnectedAsync</c> throw after its DB write has succeeded.</summary>
    private sealed class ThrowingStatusNotifier : ITargetStatusNotifier
    {
        public event Action<Guid, TargetStatus, DateTimeOffset?>? TargetStatusChanged;

        public void Publish(Guid targetId, TargetStatus status, DateTimeOffset? lastSeenUtc)
        {
            TargetStatusChanged?.Invoke(targetId, status, lastSeenUtc);
            throw new InvalidOperationException("a status subscriber faulted");
        }
    }

    private sealed class ListLogger : ILogger<AgentHub>
    {
        internal List<string> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (logLevel >= LogLevel.Error)
            {
                Errors.Add(formatter(state, exception));
            }
        }
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

/// <summary>
/// Hands the E7 reconcile a scope whose <c>IHubContext&lt;AgentHub, IAgentHubClient&gt;</c>
/// records the cancels it pushes, so a test can assert the push happened.
/// </summary>
file sealed class StubScopeFactory(RecordingAgentHubContext hub) : IServiceScopeFactory
{
    public IServiceScope CreateScope() => new Scope(hub);

    private sealed class Scope(RecordingAgentHubContext hub) : IServiceScope, IServiceProvider
    {
        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type serviceType)
            => serviceType == typeof(IHubContext<AgentHub, IAgentHubClient>) ? hub : null;

        public void Dispose() { }
    }
}

file sealed class RecordingAgentHubContext : IHubContext<AgentHub, IAgentHubClient>
{
    private readonly AgentClients _clients;

    internal RecordingAgentHubContext() => _clients = new AgentClients(Cancelled);

    internal List<Guid> Cancelled { get; } = [];

    public IHubClients<IAgentHubClient> Clients => _clients;

    public IGroupManager Groups => throw new NotSupportedException();

    private sealed class AgentClients(List<Guid> cancelled) : IHubClients<IAgentHubClient>
    {
        private readonly IAgentHubClient _sink = new Sink(cancelled);
        public IAgentHubClient All => _sink;
        public IAgentHubClient AllExcept(IReadOnlyList<string> excluded) => _sink;
        public IAgentHubClient Client(string connectionId) => _sink;
        public IAgentHubClient Clients(IReadOnlyList<string> connectionIds) => _sink;
        public IAgentHubClient Group(string groupName) => _sink;
        public IAgentHubClient GroupExcept(string groupName, IReadOnlyList<string> excluded) => _sink;
        public IAgentHubClient Groups(IEnumerable<string> groupNames) => _sink;
        public IAgentHubClient Groups(IReadOnlyList<string> groupNames) => _sink;
        public IAgentHubClient User(string userId) => _sink;
        public IAgentHubClient Users(IEnumerable<string> userIds) => _sink;
        public IAgentHubClient Users(IReadOnlyList<string> userIds) => _sink;
    }

    private sealed class Sink(List<Guid> cancelled) : IAgentHubClient
    {
        public Task RunDeploymentAsync(KrakenDeploy.Contracts.DeploymentPlan plan)
            => throw new NotSupportedException();

        public Task RunAdhocScriptAsync(KrakenDeploy.Contracts.Adhoc.AdhocScriptCommand command)
            => throw new NotSupportedException();

        public Task PingAsync() => Task.CompletedTask;

        public Task CancelDeploymentAsync(Guid taskId, string? reason)
        {
            lock (cancelled) { cancelled.Add(taskId); }
            return Task.CompletedTask;
        }
    }
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
