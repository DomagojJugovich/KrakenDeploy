namespace KrakenDeploy.Server.Transport;

/// <summary>
/// Tracks the live mapping between SignalR connection IDs and deployment target IDs.
/// In-memory for M1; swap for a Redis-backed implementation for scale-out.
/// </summary>
public interface IAgentConnectionRegistry
{
    /// <summary>
    /// Records a new agent connection. <paramref name="accountId"/> is the business
    /// account the connection resolved to at connect (host-derived, multi-account);
    /// <c>Guid.Empty</c> in single-instance mode. Recorded so dispatch can assert a
    /// target's live connection belongs to the dispatching account (P3-8 Phase 5
    /// cross-account guard). <paramref name="abort"/> forcibly closes THIS
    /// connection (the hub's <c>Context.Abort</c>); it powers immediate agent-token
    /// revocation (A8/T1-12) so a revoked agent's live tunnel drops at once rather
    /// than surviving until its next reconnect.
    /// </summary>
    void Add(string connectionId, Guid targetId, Guid accountId = default, Action? abort = null);

    /// <summary>
    /// F5 — marks <paramref name="connectionId"/> as having PASSED <c>RegisterAsync</c>,
    /// wire-contract version check included. Until this is called the connection is
    /// tracked but NOT DISPATCHABLE: <see cref="GetConnectionId"/> ignores it.
    /// <para>
    /// <see cref="HasConnectionFor"/> deliberately does NOT — it answers LIVENESS, a
    /// different question, and conflating the two lets the mid-wave disconnect monitor
    /// diagnose "agent disconnected" against a healthy agent still inside its
    /// connect→register window. See its own remarks; that distinction is load-bearing in
    /// both directions and any other implementation of this interface must preserve it.
    /// </para>
    /// <para>
    /// The split exists because <c>OnConnectedAsync</c> has to register the connection
    /// before the agent can invoke anything, so "connected" and "version-verified" are
    /// genuinely different states, and dispatch must key on the second. A no-op for a
    /// connection that is no longer tracked, so a refusal path that already removed it
    /// cannot resurrect it.
    /// </para>
    /// </summary>
    void MarkRegistered(string connectionId);

    /// <summary>
    /// Removes the connection and returns the associated target ID.
    /// Returns <c>false</c> if the connection was not tracked.
    /// <para>
    /// E4 — the target→connection mapping is removed compare-and-remove: only
    /// when it still points at <paramref name="connectionId"/>. A late,
    /// out-of-order disconnect of a superseded connection therefore cannot wipe
    /// the mapping a reconnected agent already re-registered.
    /// </para>
    /// </summary>
    bool TryRemove(string connectionId, out Guid targetId);

    /// <summary>
    /// E4 backstop — re-affirm the target→connection mapping for a connection
    /// that is still registered (present in the connection index) but whose
    /// target mapping was wiped by a late, out-of-order removal of a superseded
    /// connection. Called from the heartbeat path so a wiped mapping self-heals
    /// within one heartbeat. No-op (returns <c>false</c>) when the connection is
    /// no longer registered — the backstop heals asymmetric drops, it never
    /// resurrects a connection removed on purpose (contract refusal / token
    /// revocation). Returns <c>true</c> when it actually restored a missing
    /// mapping.
    /// </summary>
    bool Reaffirm(string connectionId, Guid targetId, Guid accountId = default, Action? abort = null);

    /// <summary>Returns true if <paramref name="targetId"/> has at least one active connection.</summary>
    bool HasConnectionFor(Guid targetId);

    /// <summary>
    /// Forcibly closes the target's live connection if one is tracked (A8/T1-12
    /// agent-token revocation). Returns <c>true</c> if a connection was aborted,
    /// <c>false</c> if the target had none (already offline). Node-local, like the
    /// rest of the registry: revocation still takes effect for a connection on
    /// another node via the version check on that node's next connect/call.
    /// </summary>
    bool AbortConnectionFor(Guid targetId);

    /// <summary>Returns the target ID for a connection, or <c>null</c> if not found.</summary>
    Guid? GetTargetId(string connectionId);

    /// <summary>Returns the connection ID for a target, or <c>null</c> if offline.</summary>
    string? GetConnectionId(Guid targetId);

    /// <summary>
    /// Returns the business account recorded for the target's live connection
    /// (<c>Guid.Empty</c> in single-instance mode), or <c>null</c> if the target has
    /// no active connection. Used by the dispatch cross-account guard.
    /// </summary>
    Guid? GetAccountForTarget(Guid targetId);

    /// <summary>Total number of currently connected agents.</summary>
    int Count { get; }
}
