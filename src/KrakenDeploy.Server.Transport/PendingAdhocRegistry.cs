using System.Collections.Concurrent;
using KrakenDeploy.Contracts.Adhoc;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// M11.E.7 — shared singleton state coordinating ad-hoc-script dispatches
/// between <see cref="AdhocDispatcher"/> (writer) and <see cref="AgentHub"/>
/// (reader). Mirrors the <see cref="IPendingSubPlanRegistry"/> shape but keyed
/// by <c>(sessionId, iterNumber, targetId)</c> instead of
/// <c>(deploymentId, targetId)</c> — adhoc has no deployment row.
/// <para>
/// When the dispatcher fans an iteration's signed script out to its frozen
/// target set, it registers one TCS per target here. The agent calls
/// <see cref="IAgentHubServer.ReportAdhocResultAsync"/> when it finishes (or
/// refuses to run); the hub resolves the connection's target id and routes
/// the result to the matching slot. The dispatcher awaits all per-target TCSs
/// and returns the collated result list.
/// </para>
/// </summary>
public interface IPendingAdhocRegistry
{
    /// <summary>Register a TCS that <see cref="TryResolve"/> will complete
    /// when the agent reports the per-target outcome.</summary>
    void Register(
        Guid sessionId, int iterNumber, Guid targetId,
        TaskCompletionSource<AdhocScriptResult> tcs);

    /// <summary>Resolve a pending TCS if one is registered. Returns
    /// <c>true</c> when a slot was waiting; <c>false</c> when the result
    /// arrived late (slot already cancelled or never opened) — the hub
    /// silently drops it in that case.</summary>
    bool TryResolve(
        Guid sessionId, int iterNumber, Guid targetId, AdhocScriptResult result);

    /// <summary>Forcefully cancel a pending TCS (dispatcher cleanup path).
    /// The TCS resolves with an <see cref="AdhocScriptResult"/> whose
    /// <see cref="AdhocScriptResult.AgentError"/> is set to
    /// <paramref name="reason"/>.</summary>
    void Cancel(Guid sessionId, int iterNumber, Guid targetId, string reason);
}

public sealed class PendingAdhocRegistry : IPendingAdhocRegistry
{
    private readonly ConcurrentDictionary<
        (Guid SessionId, int IterNumber, Guid TargetId),
        TaskCompletionSource<AdhocScriptResult>> _pending = new();

    public void Register(
        Guid sessionId, int iterNumber, Guid targetId,
        TaskCompletionSource<AdhocScriptResult> tcs)
    {
        ArgumentNullException.ThrowIfNull(tcs);
        _pending[(sessionId, iterNumber, targetId)] = tcs;
    }

    public bool TryResolve(
        Guid sessionId, int iterNumber, Guid targetId, AdhocScriptResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (_pending.TryRemove((sessionId, iterNumber, targetId), out var tcs))
        {
            tcs.TrySetResult(result);
            return true;
        }
        return false;
    }

    public void Cancel(Guid sessionId, int iterNumber, Guid targetId, string reason)
    {
        if (_pending.TryRemove((sessionId, iterNumber, targetId), out var tcs))
        {
            tcs.TrySetResult(new AdhocScriptResult(
                SessionId:  sessionId,
                IterNumber: iterNumber,
                ExitCode:   -1,
                Stdout:     string.Empty,
                Stderr:     string.Empty,
                AgentError: reason));
        }
    }
}
