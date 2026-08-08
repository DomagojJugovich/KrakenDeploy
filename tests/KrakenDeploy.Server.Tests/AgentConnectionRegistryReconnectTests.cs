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
    // ── A tracked connection IS a dispatchable one ───────────────────────────

    [Fact]
    public void A_tracked_connection_is_immediately_dispatchable()
    {
        // Liveness and eligibility are the same question again, and that is the point.
        // There used to be a second predicate here — a "has completed RegisterAsync" flag —
        // because the wire-contract check lived in a hub METHOD, so the server had to admit
        // a connection before it could learn the version. That check now runs on the
        // handshake (AgentContractHandshakeGate refuses a skew with 426), and
        // OnConnectedAsync only calls Add once the target is positively resolved in the
        // right account. So there is no window left to guard: past Add, the connection is
        // verified.
        //
        // Keeping the two predicates in agreement matters in both directions. Gating
        // eligibility flipped healthy targets Offline inside the old window; gating LIVENESS
        // let B3's mid-wave disconnect monitor diagnose "agent disconnected" against an
        // agent that was still executing, which under Atomic failure mode triggers
        // farm-wide cleanup.
        var registry = new InMemoryAgentConnectionRegistry();
        var targetId = Guid.NewGuid();

        registry.Add("conn-1", targetId);

        registry.GetTargetId("conn-1").Should().Be(targetId);
        registry.GetConnectionId(targetId).Should().Be("conn-1",
            "a connection the hub accepted has already passed the handshake contract gate");
        registry.HasConnectionFor(targetId).Should().BeTrue();
    }

    [Fact]
    public void A_removed_connection_is_not_dispatchable()
    {
        // Removal is how a revoked token (A8/T1-12) and a retired target take a live tunnel
        // out of service, so it must be immediate and must not be recoverable by anything
        // other than a fresh connection.
        var registry = new InMemoryAgentConnectionRegistry();
        var targetId = Guid.NewGuid();

        registry.Add("conn-1", targetId);
        registry.TryRemove("conn-1", out _).Should().BeTrue();

        registry.GetConnectionId(targetId).Should().BeNull();
        registry.HasConnectionFor(targetId).Should().BeFalse();
    }

    [Fact]
    public void A_reconnect_restores_dispatchability()
    {
        // The ordinary reconnect: the old connection is removed, OnConnectedAsync adds the
        // new one, and work flows again with no second step required.
        var registry = new InMemoryAgentConnectionRegistry();
        var targetId = Guid.NewGuid();

        registry.Add("conn-1", targetId);
        registry.TryRemove("conn-1", out _);

        registry.Add("conn-2", targetId);

        registry.GetConnectionId(targetId).Should().Be("conn-2");
    }

    [Fact]
    public void Late_disconnect_of_superseded_connection_keeps_the_live_mapping()
    {
        var registry = new InMemoryAgentConnectionRegistry();
        var targetId = Guid.NewGuid();

        registry.Add("old-conn", targetId);
        // Agent reconnects: the new connection registers under the SAME target
        // before the old connection's late OnDisconnected fires.
        registry.Add("new-conn", targetId);

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

        registry.Add("old-conn", targetId, Guid.Empty, abort: () => oldAborts++);
        registry.Add("new-conn", targetId, Guid.Empty, abort: () => newAborts++);

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

        registry.Add("old-conn", targetId, oldAccount);
        registry.Add("new-conn", targetId, newAccount);

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
        registry.Add("conn-1", targetId);

        registry.TryRemove("conn-1", out _).Should().BeTrue();

        registry.HasConnectionFor(targetId).Should().BeFalse();
        registry.GetConnectionId(targetId).Should().BeNull();
    }

    [Fact]
    public void Reaffirm_restores_a_mapping_wiped_while_the_connection_is_registered()
    {
        var registry = new InMemoryAgentConnectionRegistry();
        var targetId = Guid.NewGuid();

        registry.Add("conn-1", targetId);
        // conn-2 briefly becomes the target's mapping, then is removed — its
        // compare-and-remove wipes the shared target entry, but conn-1 is still
        // registered (a wiped mapping for a live connection).
        registry.Add("conn-2", targetId);
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
        registry.Add("conn-1", targetId);

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

        registry.Add("old-conn", targetId, Guid.Empty, abort: () => oldAborts++);
        registry.Add("new-conn", targetId, Guid.Empty, abort: () => newAborts++);

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
        registry.Add("conn-1", targetId);
        registry.TryRemove("conn-1", out _).Should().BeTrue();

        registry.Reaffirm("conn-1", targetId).Should().BeFalse();
        registry.HasConnectionFor(targetId).Should().BeFalse();
    }
}
