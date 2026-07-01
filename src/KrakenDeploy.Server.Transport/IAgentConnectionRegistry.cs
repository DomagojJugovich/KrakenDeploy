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
    /// cross-account guard).
    /// </summary>
    void Add(string connectionId, Guid targetId, Guid accountId = default);

    /// <summary>
    /// Removes the connection and returns the associated target ID.
    /// Returns <c>false</c> if the connection was not tracked.
    /// </summary>
    bool TryRemove(string connectionId, out Guid targetId);

    /// <summary>Returns true if <paramref name="targetId"/> has at least one active connection.</summary>
    bool HasConnectionFor(Guid targetId);

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
