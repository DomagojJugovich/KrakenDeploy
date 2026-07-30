using FluentAssertions;
using KrakenDeploy.Server.Transport;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// E4 — the connection registry must survive an asymmetric SignalR drop: the
/// agent reconnects (a NEW connection registers under the same target) BEFORE
/// the OLD connection's late <c>OnDisconnectedAsync</c> fires (SignalR's
/// ~30 s <c>ClientTimeoutInterval</c>). <see cref="IAgentConnectionRegistry.TryRemove"/>
/// must compare-and-remove so the late disconnect cannot wipe the LIVE mapping,
/// and the heartbeat <see cref="IAgentConnectionRegistry.Reaffirm"/> backstop must
/// self-heal a mapping wiped by any other path — without resurrecting a
/// connection removed on purpose.
/// </summary>
public sealed class AgentConnectionRegistryReconnectTests
{
    // ── F5: dispatch eligibility requires a PASSED registration ─────────────

    [Fact]
    public void A_connected_but_unregistered_connection_is_not_dispatchable()
    {
        // OnConnectedAsync must add the connection before the agent can invoke
        // anything, so the wire-contract version is unchecked at that point. Dispatch
        // therefore keys on MarkRegistered, not on Add: a v2 agent reads v3's
        // AllowParallelTaskExecution = true as "skip the machine gate entirely", so
        // handing it work in this window would run an approved script with no lock at
        // all while the server believed the gate was honoured.
        var registry = new InMemoryAgentConnectionRegistry();
        var targetId = Guid.NewGuid();

        registry.Add("conn-1", targetId);

        registry.GetTargetId("conn-1").Should().Be(targetId,
            "the connection IS tracked — the hub needs it to answer RegisterAsync");
        registry.GetConnectionId(targetId).Should().BeNull(
            "but it is NOT dispatchable until RegisterAsync has passed");

        // LIVENESS must stay true throughout. This is the distinction that matters most:
        // HasConnectionFor answers "did the agent reconnect / is it still there", and its
        // consumers are the hub's 30 s offline grace and B3's mid-wave disconnect monitor.
        // Gating it on registration flipped healthy targets Offline and let the monitor
        // CANCEL a wave still executing on a connected agent.
        registry.HasConnectionFor(targetId).Should().BeTrue(
            "the agent is connected — liveness is not the same question as eligibility");

        registry.MarkRegistered("conn-1");

        registry.GetConnectionId(targetId).Should().Be("conn-1");
        registry.HasConnectionFor(targetId).Should().BeTrue();
    }

    [Fact]
    public void A_removed_connection_cannot_be_marked_registered()
    {
        // The contract refusal removes the connection and THEN the agent could, in
        // principle, still complete an in-flight RegisterAsync. Marking a removed
        // connection registered would resurrect a deliberately-refused agent as
        // dispatchable.
        var registry = new InMemoryAgentConnectionRegistry();
        var targetId = Guid.NewGuid();

        registry.Add("conn-1", targetId);
        registry.TryRemove("conn-1", out _).Should().BeTrue();

        registry.MarkRegistered("conn-1");

        registry.GetConnectionId(targetId).Should().BeNull(
            "a refused connection must stay undispatchable");
    }

    [Fact]
    public void Re_adding_a_connection_id_starts_unregistered_again()
    {
        // A refused agent reconnects on the auth-failure lane and OnConnectedAsync
        // re-Adds it. That must reopen the unregistered state, or the second cycle
        // would inherit the first cycle's eligibility.
        var registry = new InMemoryAgentConnectionRegistry();
        var targetId = Guid.NewGuid();

        registry.Add("conn-1", targetId);
        registry.MarkRegistered("conn-1");
        registry.TryRemove("conn-1", out _);

        registry.Add("conn-1", targetId);

        registry.GetConnectionId(targetId).Should().BeNull(
            "the new connection cycle must prove its contract version again");
    }

    [Fact]
    public void Late_disconnect_of_superseded_connection_keeps_the_live_mapping()
    {
        var registry = new InMemoryAgentConnectionRegistry();
        var targetId = Guid.NewGuid();

        registry.AddRegistered("old-conn", targetId);
        // Agent reconnects: the new connection registers under the SAME target
        // before the old connection's late OnDisconnected fires.
        registry.AddRegistered("new-conn", targetId);

        // Late, out-of-order disconnect of the OLD connection.
        registry.TryRemove("old-conn", out var removed).Should().BeTrue();
        removed.Should().Be(targetId);

        // The healthy agent must stay visible under its live connection —
        // otherwise it goes false-Offline, its waves are killed after the grace,
        // and cancel pushes / token revocation silently no-op.
        registry.HasConnectionFor(targetId).Should().BeTrue();
        registry.GetConnectionId(targetId).Should().Be("new-conn");
        registry.GetTargetId("new-conn").Should().Be(targetId);
        registry.GetTargetId("old-conn").Should().BeNull();
    }

    [Fact]
    public void Late_disconnect_preserves_the_live_connections_abort_delegate()
    {
        var registry = new InMemoryAgentConnectionRegistry();
        var targetId = Guid.NewGuid();
        var oldAborts = 0;
        var newAborts = 0;

        registry.AddRegistered("old-conn", targetId, Guid.Empty, abort: () => oldAborts++);
        registry.AddRegistered("new-conn", targetId, Guid.Empty, abort: () => newAborts++);

        // Late disconnect of the superseded connection must not strip the live
        // connection's abort delegate (A8/T1-12 token revocation depends on it).
        registry.TryRemove("old-conn", out _).Should().BeTrue();

        registry.AbortConnectionFor(targetId).Should().BeTrue();
        newAborts.Should().Be(1);
        oldAborts.Should().Be(0);
    }

    [Fact]
    public void Late_disconnect_preserves_the_live_connections_account()
    {
        var registry = new InMemoryAgentConnectionRegistry();
        var targetId = Guid.NewGuid();
        var oldAccount = Guid.NewGuid();
        var newAccount = Guid.NewGuid();

        registry.AddRegistered("old-conn", targetId, oldAccount);
        registry.AddRegistered("new-conn", targetId, newAccount);

        registry.TryRemove("old-conn", out _).Should().BeTrue();

        registry.GetAccountForTarget(targetId).Should().Be(newAccount);
    }

    [Fact]
    public void TryRemove_of_the_live_connection_still_clears_the_mapping()
    {
        // No supersede: removing the connection that currently owns the mapping
        // must clear it (the compare-and-remove matches).
        var registry = new InMemoryAgentConnectionRegistry();
        var targetId = Guid.NewGuid();
        registry.AddRegistered("conn-1", targetId);

        registry.TryRemove("conn-1", out _).Should().BeTrue();

        registry.HasConnectionFor(targetId).Should().BeFalse();
        registry.GetConnectionId(targetId).Should().BeNull();
    }

    [Fact]
    public void Reaffirm_restores_a_mapping_wiped_while_the_connection_is_registered()
    {
        var registry = new InMemoryAgentConnectionRegistry();
        var targetId = Guid.NewGuid();

        registry.AddRegistered("conn-1", targetId);
        // conn-2 briefly becomes the target's mapping, then is removed — its
        // compare-and-remove wipes the shared target entry, but conn-1 is still
        // registered (a wiped mapping for a live connection).
        registry.AddRegistered("conn-2", targetId);
        registry.TryRemove("conn-2", out _).Should().BeTrue();
        registry.HasConnectionFor(targetId).Should().BeFalse();
        registry.GetTargetId("conn-1").Should().Be(targetId);

        // conn-1's next heartbeat reaffirms and heals the wiped mapping.
        registry.Reaffirm("conn-1", targetId).Should().BeTrue();
        registry.HasConnectionFor(targetId).Should().BeTrue();
        registry.GetConnectionId(targetId).Should().Be("conn-1");
    }

    [Fact]
    public void Reaffirm_is_a_noop_for_an_intact_mapping()
    {
        var registry = new InMemoryAgentConnectionRegistry();
        var targetId = Guid.NewGuid();
        registry.AddRegistered("conn-1", targetId);

        registry.Reaffirm("conn-1", targetId).Should().BeFalse(
            "an intact mapping needs no healing");
        registry.GetConnectionId(targetId).Should().Be("conn-1");
    }

    [Fact]
    public void Reaffirm_does_not_clobber_a_newer_live_connection()
    {
        // A stalled heartbeat from a SUPERSEDED connection (its OnDisconnected
        // has not yet fired, so it is still in the connection index) must not
        // steal the target mapping back from — or replace the abort delegate of —
        // the newer connection that already took over.
        var registry = new InMemoryAgentConnectionRegistry();
        var targetId = Guid.NewGuid();
        var oldAborts = 0;
        var newAborts = 0;

        registry.AddRegistered("old-conn", targetId, Guid.Empty, abort: () => oldAborts++);
        registry.AddRegistered("new-conn", targetId, Guid.Empty, abort: () => newAborts++);

        registry.Reaffirm("old-conn", targetId).Should().BeFalse(
            "the mapping already points at the newer live connection");
        registry.GetConnectionId(targetId).Should().Be("new-conn");

        registry.AbortConnectionFor(targetId).Should().BeTrue();
        newAborts.Should().Be(1);
        oldAborts.Should().Be(0);
    }

    [Fact]
    public void Reaffirm_never_resurrects_a_removed_connection()
    {
        // A connection removed on purpose (contract refusal / token revocation)
        // must not be brought back by a straggling heartbeat.
        var registry = new InMemoryAgentConnectionRegistry();
        var targetId = Guid.NewGuid();
        registry.AddRegistered("conn-1", targetId);
        registry.TryRemove("conn-1", out _).Should().BeTrue();

        registry.Reaffirm("conn-1", targetId).Should().BeFalse();
        registry.HasConnectionFor(targetId).Should().BeFalse();
    }
}
