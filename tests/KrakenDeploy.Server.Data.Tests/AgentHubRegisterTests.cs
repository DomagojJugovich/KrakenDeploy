using System.Security.Claims;
using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Transport;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
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
        // Roles = [] — server-side roles should be preserved when agent sends empty list.
        await hub.RegisterAsync(new AgentRegistrationRequest(
            target.Id, "test-machine", "Linux 6.0", "1.0.0", [], 0L, 0L));

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

        await BuildHub(postgres, target.Id).RegisterAsync(
            new AgentRegistrationRequest(target.Id, "m", "o", "v", [], 0L, 0L));

        await db.Entry(target).ReloadAsync();
        string[] expectedRoles = ["wizard-role"];
        target.Roles.Should().Equal(expectedRoles,
            because: "an empty role list from the agent must not overwrite server-configured roles");
    }

    [Fact]
    public async Task RegisterAsync_overwrites_roles_when_agent_sends_non_empty_list()
    {
        await using var db = postgres.CreateContext();

        var target = new DeploymentTarget
        {
            Name = "hub-test-roles-overwrite",
            Roles = ["old-role"],
            TransportMode = TransportMode.Reverse,
        };
        db.DeploymentTargets.Add(target);
        await db.SaveChangesAsync();

        await BuildHub(postgres, target.Id).RegisterAsync(
            new AgentRegistrationRequest(target.Id, "m", "o", "v",
                ["role-a", "role-b"], 0L, 0L));

        await db.Entry(target).ReloadAsync();
        target.Roles.Should().Equal("role-a", "role-b");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static AgentHub BuildHub(PostgresFixture postgres, Guid targetId)
    {
        var publisher = new TargetStatusPublisher(
            new InMemoryTargetStatusNotifier(),
            new NullUiHubContext(),
            NullLogger<TargetStatusPublisher>.Instance);

        var hub = new AgentHub(
            new InMemoryAgentConnectionRegistry(),
            postgres,
            new NeverUsedScopeFactory(),
            publisher,
            TimeProvider.System,
            new NullUiHubContext(),
            new NeverUsedPendingSubPlanRegistry(),
            new NeverUsedPendingAdhocRegistry(),
            new KrakenDeploy.Server.Data.Accounts.DisabledAccountContext(),
            NullLogger<AgentHub>.Instance);

        hub.Context = new FakeHubCallerContext(targetId);
        return hub;
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
    public void Register(Guid deploymentId, Guid targetId, TaskCompletionSource<SubPlanResult> tcs)
        => throw new NotSupportedException("IPendingSubPlanRegistry is not used by RegisterAsync.");

    public bool TryResolve(Guid deploymentId, Guid targetId, SubPlanResult result)
        => throw new NotSupportedException("IPendingSubPlanRegistry is not used by RegisterAsync.");

    public void Cancel(Guid deploymentId, Guid targetId, string reason)
        => throw new NotSupportedException("IPendingSubPlanRegistry is not used by RegisterAsync.");

    public void RecordStepResult(Guid deploymentId, Guid targetId, SubPlanStepResult result)
        => throw new NotSupportedException("IPendingSubPlanRegistry is not used by RegisterAsync.");

    public IReadOnlyList<SubPlanStepResult> DrainStepResults(Guid deploymentId, Guid targetId)
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
