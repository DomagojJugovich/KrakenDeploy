using System.Collections.Concurrent;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// Thread-safe in-memory implementation of <see cref="IAgentConnectionRegistry"/>.
/// Suitable for single-server deployments (M1). Replace with a Redis-backed
/// implementation for multi-node scale-out.
/// </summary>
public sealed class InMemoryAgentConnectionRegistry : IAgentConnectionRegistry
{
    // Both dictionaries are kept in sync; updated together under no explicit lock
    // because ConcurrentDictionary operations are individually atomic and the
    // read/write asymmetry is acceptable (a brief inconsistency window is harmless).
    private readonly ConcurrentDictionary<string, Guid> _byConnection = new();
    private readonly ConcurrentDictionary<Guid, string> _byTarget = new();
    private readonly ConcurrentDictionary<Guid, Guid> _accountByTarget = new();
    // Abort delegate (the hub's Context.Abort) per target, for A8/T1-12 revocation.
    private readonly ConcurrentDictionary<Guid, Action> _abortByTarget = new();

    public void Add(string connectionId, Guid targetId, Guid accountId = default, Action? abort = null)
    {
        _byConnection[connectionId] = targetId;
        _byTarget[targetId] = connectionId;
        _accountByTarget[targetId] = accountId;
        if (abort is not null)
        {
            _abortByTarget[targetId] = abort;
        }
    }

    public bool TryRemove(string connectionId, out Guid targetId)
    {
        if (!_byConnection.TryRemove(connectionId, out targetId))
        {
            return false;
        }

        // E4 — compare-and-remove the target mapping: wipe it (and its account /
        // abort side-tables) ONLY if it still points at THIS connection. On an
        // asymmetric drop the agent reconnects — OnConnectedAsync registers the
        // NEW connection under the same target — BEFORE this late
        // OnDisconnectedAsync of the OLD connection fires (SignalR's
        // ClientTimeoutInterval, ~30 s). An unconditional remove here would then
        // wipe the LIVE mapping, making a healthy agent invisible (false Offline,
        // waves killed after the disconnect grace, cancel pushes and token
        // revocation silently no-op). The side-tables are keyed by target, not
        // connection, so they must only be dropped when we actually own the
        // mapping — hence they are gated on the target-mapping removal winning.
        if (_byTarget.TryRemove(new KeyValuePair<Guid, string>(targetId, connectionId)))
        {
            _accountByTarget.TryRemove(targetId, out _);
            _abortByTarget.TryRemove(targetId, out _);
        }
        return true;
    }

    public bool Reaffirm(string connectionId, Guid targetId, Guid accountId = default, Action? abort = null)
    {
        // E4 backstop — called on each heartbeat. If a late, out-of-order removal
        // of a superseded connection (or any future path) wiped this target's
        // mapping, restore it so the agent cannot stay falsely invisible for
        // longer than one heartbeat interval. Guarded by _byConnection membership
        // so a connection removed on purpose (contract refusal / token
        // revocation) is NEVER resurrected.
        if (!_byConnection.ContainsKey(connectionId))
        {
            return false;
        }

        // Restore the mapping ONLY when it is genuinely wiped (absent). TryAdd is
        // the atomic "set iff absent", so a *stalled* heartbeat from a superseded
        // connection cannot clobber a NEWER live connection that has already taken
        // over the target (which would strip that connection's A8 abort delegate
        // and re-hide it). An intact mapping — whether this connection's own or a
        // successor's — is left untouched; only the wiped case restores the
        // account / abort side-tables (TryRemove drops all three together, so a
        // present target mapping always has its side-tables intact).
        if (_byTarget.TryAdd(targetId, connectionId))
        {
            _accountByTarget[targetId] = accountId;
            if (abort is not null)
            {
                _abortByTarget[targetId] = abort;
            }
            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the target has a live connection. Since the wire-contract check moved onto
    /// the handshake there is no longer a second, narrower notion of "eligible": a tracked
    /// connection IS a dispatchable one, so this and <see cref="GetConnectionId"/> answer
    /// the same question and cannot drift apart.
    /// </summary>
    public bool HasConnectionFor(Guid targetId) => _byTarget.ContainsKey(targetId);

    public bool AbortConnectionFor(Guid targetId)
    {
        if (!_abortByTarget.TryGetValue(targetId, out var abort))
        {
            return false;
        }

        abort();
        return true;
    }

    public Guid? GetTargetId(string connectionId)
        => _byConnection.TryGetValue(connectionId, out var id) ? id : null;

    /// <summary>
    /// The target's dispatchable connection, or <c>null</c>. Every tracked connection is
    /// dispatchable: <see cref="Add"/> is the LAST statement of <c>OnConnectedAsync</c>, so it
    /// runs only once the target is positively resolved in the right account and every write
    /// that could throw has succeeded — and a wire-contract skew never reaches the hub at all
    /// (<c>docs/agent-wire-contract.md</c>).
    /// </summary>
    public string? GetConnectionId(Guid targetId)
        => _byTarget.TryGetValue(targetId, out var connId) ? connId : null;

    public Guid? GetAccountForTarget(Guid targetId)
        => _accountByTarget.TryGetValue(targetId, out var accountId) ? accountId : null;

    public int Count => _byConnection.Count;
}
