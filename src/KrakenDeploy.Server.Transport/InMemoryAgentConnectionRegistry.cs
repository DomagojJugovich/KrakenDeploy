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
    // F5 — connections that have PASSED RegisterAsync, including its wire-contract
    // version check. Membership is the DISPATCH gate; see MarkRegistered.
    private readonly ConcurrentDictionary<string, bool> _registered = new();

    public void Add(string connectionId, Guid targetId, Guid accountId = default, Action? abort = null)
    {
        _byConnection[connectionId] = targetId;
        _byTarget[targetId] = connectionId;
        _accountByTarget[targetId] = accountId;
        if (abort is not null)
        {
            _abortByTarget[targetId] = abort;
        }
        // Deliberately NOT registered yet: OnConnectedAsync calls this, and the
        // contract-version check does not run until RegisterAsync.
    }

    public void MarkRegistered(string connectionId)
    {
        // Only for a connection we still track — a refusal path that already removed
        // it must never be resurrected as dispatchable.
        if (_byConnection.ContainsKey(connectionId))
        {
            _registered[connectionId] = true;
        }
    }

    public bool TryRemove(string connectionId, out Guid targetId)
    {
        if (!_byConnection.TryRemove(connectionId, out targetId))
        {
            return false;
        }
        _registered.TryRemove(connectionId, out _);

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
    /// LIVENESS, not eligibility — deliberately NOT gated on <see cref="MarkRegistered"/>.
    /// Its consumers ask "did the agent reconnect?" (the hub's offline grace) and "is the
    /// agent still there?" (B3's mid-wave disconnect monitor). Answering those with
    /// dispatch eligibility flips a healthy target Offline during the connect→register
    /// window and, worse, lets the disconnect monitor CANCEL a wave that is still
    /// executing on a connected agent — a false "agent disconnected mid-wave" diagnosis
    /// that under Atomic failure mode triggers farm-wide cleanup. Dispatchability is
    /// <see cref="GetConnectionId"/>'s question, and only that one.
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
    /// F5 — the target's DISPATCHABLE connection, or <c>null</c>. A connection that has
    /// not yet passed <c>RegisterAsync</c> is deliberately invisible here.
    /// <para>
    /// <c>OnConnectedAsync</c> calls <see cref="Add"/> before the agent has invoked
    /// <c>RegisterAsync</c>, so the wire-contract version has not been checked yet. This
    /// used to be the ONLY dispatch predicate, which meant a version-skewed agent could
    /// be sent work in that window — and permanently, if its <c>RegisterAsync</c> invoke
    /// failed, because that failure is swallowed as retryable and re-sent only on the
    /// next reconnect. A v2 agent reads the v3 <c>AllowParallelTaskExecution = true</c>
    /// as "skip the machine gate entirely", so it would run an approved script with no
    /// lock at all while the server believed the gate was honoured. Gating the lookup
    /// fixes every dispatch consumer at once rather than each remembering to ask.
    /// </para>
    /// </summary>
    public string? GetConnectionId(Guid targetId)
        => _byTarget.TryGetValue(targetId, out var connId) && _registered.ContainsKey(connId)
            ? connId
            : null;

    public Guid? GetAccountForTarget(Guid targetId)
        => _accountByTarget.TryGetValue(targetId, out var accountId) ? accountId : null;

    public int Count => _byConnection.Count;
}
