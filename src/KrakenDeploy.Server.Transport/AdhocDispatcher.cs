using System.Text.Json;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.Adhoc;
using KrakenDeploy.Server.Core.Domain.Ai;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// M11.E.7 — fans an operator-approved + server-signed
/// <see cref="AdhocIteration"/> out to every target in the owning
/// <see cref="AdhocSession.FrozenTargetSetJson"/>, awaits each per-target
/// result via <see cref="IPendingAdhocRegistry"/>, and returns the collated
/// list. The dispatcher is the structural enforcer of
/// <strong>M11.E.15a</strong>: the frozen target set is the ONLY input — the
/// dispatcher has no surface to accept targets from anywhere else, so the LLM
/// can't expand the blast radius even if it tries to.
/// <para>
/// Agents that are offline at dispatch time get an
/// <see cref="AdhocScriptResult"/> with an immediate
/// <see cref="AdhocScriptResult.AgentError"/> ("agent offline") and the same
/// session/iteration binding, so the iteration verdict LLM can still see them.
/// </para>
/// <para>
/// Hub-push abstraction: <see cref="IAdhocAgentPusher"/> is the seam
/// production code uses against <see cref="IHubContext{THub, T}"/>; tests
/// substitute a fake so the dispatcher is exercisable without spinning up
/// SignalR.
/// </para>
/// </summary>
public interface IAdhocDispatcher
{
    /// <summary>See <see cref="AdhocDispatcher.DispatchAsync"/>.</summary>
    Task<IReadOnlyList<AdhocPerTargetResult>> DispatchAsync(
        AdhocSession session, AdhocIteration iteration,
        CancellationToken ct, TimeSpan? timeout = null);
}

/// <summary>
/// Server-side projection — pairs the wire-level <see cref="AdhocScriptResult"/>
/// with the target id the dispatcher routed it to. The wire payload doesn't
/// carry the target id (the hub recovers it from the connection's
/// <c>NameIdentifier</c> claim), but the persisted <c>ResultsJson</c> on the
/// iteration MUST carry it so the /adhoc UI can render "which target said
/// what" and the iteration-verdict LLM can attribute outcomes per target.
/// </summary>
public sealed record AdhocPerTargetResult(Guid TargetId, AdhocScriptResult Result);

public sealed class AdhocDispatcher(
    IAgentConnectionRegistry connections,
    IPendingAdhocRegistry pending,
    IAdhocAgentPusher pusher,
    ILogger<AdhocDispatcher> logger) : IAdhocDispatcher
{
    /// <summary>
    /// Default per-target wait timeout. Adhoc scripts are interactive +
    /// short-lived; if a target doesn't report in 5 minutes the dispatcher
    /// resolves its slot with an <see cref="AdhocScriptResult.AgentError"/>
    /// rather than blocking the iteration indefinitely. Iteration-level
    /// timeout policy (M13.F.3) can override this in a future pass.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Dispatches <paramref name="iteration"/> across <paramref name="session"/>'s
    /// frozen target set. Caller MUST have already populated
    /// <see cref="AdhocIteration.ScriptSignature"/> via the signing service —
    /// the dispatcher only routes; it does not sign.
    /// </summary>
    public async Task<IReadOnlyList<AdhocPerTargetResult>> DispatchAsync(
        AdhocSession session,
        AdhocIteration iteration,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(iteration);
        if (string.IsNullOrEmpty(iteration.ScriptSignature))
        {
            throw new InvalidOperationException(
                $"Cannot dispatch iteration {iteration.Id} for session {session.Id}: " +
                "the iteration has no signature. Sign with AdhocScriptSigner.Sign " +
                "before invoking the dispatcher.");
        }

        // The frozen set IS the dispatch list — no external override.
        var targetIds = ParseFrozenTargets(session);
        if (targetIds.Count == 0)
        {
            logger.LogWarning(
                "AdhocDispatcher: session {SessionId} iteration {Iter} has an empty " +
                "frozen target set — nothing to dispatch.",
                session.Id, iteration.IterNumber);
            return [];
        }

        var command = new AdhocScriptCommand(
            SessionId:  session.Id,
            IterNumber: iteration.IterNumber,
            Script:     iteration.GeneratedScript,
            Signature:  iteration.ScriptSignature!);

        var effectiveTimeout = timeout ?? DefaultTimeout;
        var tasks = new List<Task<AdhocPerTargetResult>>(targetIds.Count);
        foreach (var targetId in targetIds)
        {
            tasks.Add(DispatchToTargetAsync(
                session.Id, iteration.IterNumber, targetId, command,
                effectiveTimeout, ct));
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    private async Task<AdhocPerTargetResult> DispatchToTargetAsync(
        Guid sessionId, int iterNumber, Guid targetId,
        AdhocScriptCommand command, TimeSpan timeout, CancellationToken ct)
    {
        var connectionId = connections.GetConnectionId(targetId);
        if (connectionId is null)
        {
            // Short-circuit — no need to allocate a TCS for an offline target.
            logger.LogWarning(
                "AdhocDispatcher: target {TargetId} for session {SessionId} iter {Iter} " +
                "has no live connection; reporting agent-offline.",
                targetId, sessionId, iterNumber);
            return new AdhocPerTargetResult(targetId, new AdhocScriptResult(
                SessionId:  sessionId,
                IterNumber: iterNumber,
                ExitCode:   -1,
                Stdout:     string.Empty,
                Stderr:     string.Empty,
                AgentError: "Agent offline at dispatch."));
        }

        var tcs = new TaskCompletionSource<AdhocScriptResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        pending.Register(sessionId, iterNumber, targetId, tcs);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(timeout);

        try
        {
            await pusher.PushAsync(connectionId, command, ct).ConfigureAwait(false);

            using var ctr = linkedCts.Token.Register(
                () => pending.Cancel(
                    sessionId, iterNumber, targetId,
                    "Adhoc script timed out before the agent reported back."));
            var wireResult = await tcs.Task.ConfigureAwait(false);
            return new AdhocPerTargetResult(targetId, wireResult);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Push failed (e.g. agent disconnected mid-send). Resolve the slot
            // ourselves with an AgentError so the dispatcher's WhenAll
            // doesn't deadlock.
            logger.LogError(ex,
                "AdhocDispatcher: failed to push command to target {TargetId} for " +
                "session {SessionId} iter {Iter}.", targetId, sessionId, iterNumber);
            pending.Cancel(sessionId, iterNumber, targetId,
                $"Push failed: {ex.Message}");
            var wireResult = await tcs.Task.ConfigureAwait(false);
            return new AdhocPerTargetResult(targetId, wireResult);
        }
    }

    private static List<Guid> ParseFrozenTargets(AdhocSession session)
    {
        if (string.IsNullOrWhiteSpace(session.FrozenTargetSetJson))
        {
            return [];
        }
        try
        {
            var ids = JsonSerializer.Deserialize<List<Guid>>(session.FrozenTargetSetJson);
            return ids ?? [];
        }
        catch (JsonException)
        {
            // Corrupt frozen set — refuse to dispatch rather than guess.
            throw new InvalidOperationException(
                $"AdhocDispatcher: session {session.Id} has malformed " +
                $"FrozenTargetSetJson: '{session.FrozenTargetSetJson}'.");
        }
    }
}

/// <summary>
/// Thin abstraction over the SignalR hub's per-connection push so the
/// dispatcher can be unit-tested without an <see cref="IHubContext{THub, T}"/>.
/// </summary>
public interface IAdhocAgentPusher
{
    Task PushAsync(string connectionId, AdhocScriptCommand command, CancellationToken ct);
}

/// <summary>
/// Production <see cref="IAdhocAgentPusher"/> that resolves the connection on
/// the <see cref="AgentHub"/>'s typed-client context and invokes
/// <see cref="IAgentHubClient.RunAdhocScriptAsync"/>.
/// </summary>
public sealed class HubContextAdhocAgentPusher(
    IHubContext<AgentHub, IAgentHubClient> hub) : IAdhocAgentPusher
{
    public Task PushAsync(string connectionId, AdhocScriptCommand command, CancellationToken ct)
    {
        _ = ct; // SignalR doesn't take a CT on the per-client push surface
        return hub.Clients.Client(connectionId).RunAdhocScriptAsync(command);
    }
}
