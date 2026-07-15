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
    /// Removes the connection and returns the associated target ID.
    /// Returns <c>false</c> if the connection was not tracked.
    /// </summary>
    bool TryRemove(string connectionId, out Guid targetId);

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
