using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Transport;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Guards the multi-account isolation of <see cref="TargetStatusPublisher"/>: the
/// external SignalR push must go to the target's per-account group, never
/// <c>Clients.All</c> (which would leak one tenant's target existence/status to every
/// other tenant's browsers, since all tenants share one hub endpoint). Pure unit test —
/// no database, no transport.
/// </summary>
public class TargetStatusPublisherTests
{
    [Fact]
    public async Task PublishAsync_pushes_only_to_the_targets_account_group_never_all()
    {
        var accountId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var lastSeen = DateTimeOffset.UtcNow;

        var hub = new RecordingUiHubContext();
        var publisher = new TargetStatusPublisher(
            new InMemoryTargetStatusNotifier(),
            hub,
            NullLogger<TargetStatusPublisher>.Instance);

        await publisher.PublishAsync(targetId, TargetStatus.Online, lastSeen, accountId);

        hub.Recorder.AllInvoked.Should().BeFalse(
            "a Clients.All broadcast would leak this account's target status to every other tenant's browsers");
        hub.Recorder.GroupNames.Should().ContainSingle()
            .Which.Should().Be($"account:{accountId}");

        hub.Recorder.Pushes.Should().ContainSingle();
        var push = hub.Recorder.Pushes[0];
        push.TargetId.Should().Be(targetId);
        push.Status.Should().Be("Online");
        push.LastSeen.Should().Be(lastSeen);
    }

    [Fact]
    public async Task PublishAsync_single_instance_uses_the_empty_account_group()
    {
        var hub = new RecordingUiHubContext();
        var publisher = new TargetStatusPublisher(
            new InMemoryTargetStatusNotifier(),
            hub,
            NullLogger<TargetStatusPublisher>.Instance);

        // Guid.Empty is what AgentHub passes single-instance (no resolved account); every
        // UI connection joins that one group, so this still reaches all clients.
        await publisher.PublishAsync(Guid.NewGuid(), TargetStatus.Offline, lastSeenUtc: null, accountId: Guid.Empty);

        hub.Recorder.AllInvoked.Should().BeFalse();
        hub.Recorder.GroupNames.Should().ContainSingle()
            .Which.Should().Be($"account:{Guid.Empty}");
    }
}

// ── Test doubles ──────────────────────────────────────────────────────────────

file sealed class RecordingUiHubContext : IHubContext<UiHub, IUiHubClient>
{
    public RecordingUiHubClients Recorder { get; } = new();
    public IHubClients<IUiHubClient> Clients => Recorder;
    public IGroupManager Groups => throw new NotSupportedException(
        "TargetStatusPublisher pushes via IHubContext.Clients, not group management.");
}

file sealed class RecordingUiHubClients : IHubClients<IUiHubClient>
{
    public List<string> GroupNames { get; } = [];
    public List<(Guid TargetId, string Status, DateTimeOffset? LastSeen)> Pushes { get; } = [];
    public bool AllInvoked { get; private set; }

    // A regression to Clients.All must be observable, not throw — so the test asserts
    // AllInvoked == false with a clear message rather than catching an exception.
    public IUiHubClient All
    {
        get { AllInvoked = true; return new RecordingUiHubClient(this); }
    }

    public IUiHubClient Group(string groupName)
    {
        GroupNames.Add(groupName);
        return new RecordingUiHubClient(this);
    }

    public IUiHubClient AllExcept(IReadOnlyList<string> excluded) => throw NotUsed();
    public IUiHubClient Client(string connectionId) => throw NotUsed();
    public IUiHubClient Clients(IReadOnlyList<string> connectionIds) => throw NotUsed();
    public IUiHubClient GroupExcept(string groupName, IReadOnlyList<string> excluded) => throw NotUsed();
    public IUiHubClient Groups(IEnumerable<string> groupNames) => throw NotUsed();
    public IUiHubClient Groups(IReadOnlyList<string> groupNames) => throw NotUsed();
    public IUiHubClient User(string userId) => throw NotUsed();
    public IUiHubClient Users(IEnumerable<string> userIds) => throw NotUsed();
    public IUiHubClient Users(IReadOnlyList<string> userIds) => throw NotUsed();

    private static NotSupportedException NotUsed()
        => new("TargetStatusPublisher only targets a single account group.");
}

file sealed class RecordingUiHubClient(RecordingUiHubClients parent) : IUiHubClient
{
    public Task TargetStatusChangedAsync(Guid targetId, string status, DateTimeOffset? lastSeenUtc)
    {
        parent.Pushes.Add((targetId, status, lastSeenUtc));
        return Task.CompletedTask;
    }

    public Task DeploymentLogAppendedAsync(
        Guid deploymentId, int sequence, DateTimeOffset timestamp, string level, string message)
        => Task.CompletedTask;

    public Task DeploymentStatusChangedAsync(Guid deploymentId, string status)
        => Task.CompletedTask;
}
