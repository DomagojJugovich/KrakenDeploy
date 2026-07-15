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

        _byTarget.TryRemove(targetId, out _);
        _accountByTarget.TryRemove(targetId, out _);
        _abortByTarget.TryRemove(targetId, out _);
        return true;
    }

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

    public string? GetConnectionId(Guid targetId)
        => _byTarget.TryGetValue(targetId, out var connId) ? connId : null;

    public Guid? GetAccountForTarget(Guid targetId)
        => _accountByTarget.TryGetValue(targetId, out var accountId) ? accountId : null;

    public int Count => _byConnection.Count;
}
