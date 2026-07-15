using FluentAssertions;
using KrakenDeploy.Server.Transport;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// A8/T1-12 — the registry must be able to drop a target's live tunnel on
/// revocation. <see cref="InMemoryAgentConnectionRegistry.AbortConnectionFor"/>
/// invokes the abort delegate the hub registered (Context.Abort) and is cleared
/// when the connection is removed, so a stale abort can never fire on a
/// reconnected agent.
/// </summary>
public sealed class AgentConnectionRegistryAbortTests
{
    [Fact]
    public void AbortConnectionFor_invokes_the_registered_abort()
    {
        var registry = new InMemoryAgentConnectionRegistry();
        var targetId = Guid.NewGuid();
        var aborted = false;
        registry.Add("conn-1", targetId, Guid.Empty, abort: () => aborted = true);

        registry.AbortConnectionFor(targetId).Should().BeTrue();
        aborted.Should().BeTrue();
    }

    [Fact]
    public void AbortConnectionFor_returns_false_when_target_offline()
    {
        var registry = new InMemoryAgentConnectionRegistry();

        registry.AbortConnectionFor(Guid.NewGuid()).Should().BeFalse();
    }

    [Fact]
    public void Removed_connection_no_longer_aborts()
    {
        var registry = new InMemoryAgentConnectionRegistry();
        var targetId = Guid.NewGuid();
        var abortCalls = 0;
        registry.Add("conn-1", targetId, Guid.Empty, abort: () => abortCalls++);

        registry.TryRemove("conn-1", out _).Should().BeTrue();

        registry.AbortConnectionFor(targetId).Should().BeFalse();
        abortCalls.Should().Be(0);
    }
}
