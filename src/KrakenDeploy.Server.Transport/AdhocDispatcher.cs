using System.Text.Json;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.Adhoc;
using KrakenDeploy.Server.Core.Domain.Ai;
using KrakenDeploy.Server.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    /// <summary>See <see cref="AdhocDispatcher.DispatchAsync"/>. <paramref name="dispatchAccountId"/>
    /// is the dispatching business account (multi-account) or <see cref="Guid.Empty"/>
    /// (single-instance); used to fail-closed against a target whose live connection
    /// belongs to a different account.</summary>
    Task<IReadOnlyList<AdhocPerTargetResult>> DispatchAsync(
        AdhocSession session, AdhocIteration iteration, Guid dispatchAccountId,
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
    ITargetConcurrencyPolicy concurrencyPolicy,
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
        Guid dispatchAccountId,
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

        // F2 — "Allow parallel task execution" is PER TARGET, so the command is
        // stamped per target even though the signed script text is shared. One read
        // for the whole frozen set. A target missing from the map (deleted since the
        // set was frozen) falls back to false = take the machine gate, the safe
        // default.
        var parallelByTarget = await concurrencyPolicy
            .GetAllowParallelAsync(targetIds, ct).ConfigureAwait(false);

        var command = new AdhocScriptCommand(
            SessionId:  session.Id,
            IterNumber: iteration.IterNumber,
            Script:     iteration.GeneratedScript,
            Signature:  iteration.ScriptSignature!);

        var effectiveTimeout = timeout ?? DefaultTimeout;
        var tasks = new List<Task<AdhocPerTargetResult>>(targetIds.Count);
        foreach (var targetId in targetIds)
        {
            var perTargetCommand = command with
            {
                AllowParallelTaskExecution =
                    parallelByTarget.TryGetValue(targetId, out var allow) && allow,
            };
            tasks.Add(DispatchToTargetAsync(
                session.Id, iteration.IterNumber, targetId, dispatchAccountId, perTargetCommand,
                effectiveTimeout, ct));
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    private async Task<AdhocPerTargetResult> DispatchToTargetAsync(
        Guid sessionId, int iterNumber, Guid targetId, Guid dispatchAccountId,
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

        // P3-8 Phase 5 — cross-account dispatch guard (parity with DeploymentWorker).
        // A live connection whose recorded account differs from the
        // dispatching account must never receive the script. Defense-in-depth — target
        // ids are globally unique and validated at agent connect — so return a per-target
        // AgentError (not throw) to match the offline short-circuit and keep Task.WhenAll
        // from deadlocking. Guid.Empty (single-instance, or an account-less connection) =>
        // skip the guard.
        if (dispatchAccountId != Guid.Empty
            && connections.GetAccountForTarget(targetId) != dispatchAccountId)
        {
            logger.LogError(
                "AdhocDispatcher: cross-account dispatch blocked for target {TargetId} " +
                "(session {SessionId} iter {Iter}) — connection account {ConnAccount} is not " +
                "the dispatch account {DispatchAccount}.",
                targetId, sessionId, iterNumber,
                connections.GetAccountForTarget(targetId), dispatchAccountId);
            return new AdhocPerTargetResult(targetId, new AdhocScriptResult(
                SessionId:  sessionId,
                IterNumber: iterNumber,
                ExitCode:   -1,
                Stdout:     string.Empty,
                Stderr:     string.Empty,
                AgentError: "Cross-account connection blocked at dispatch."));
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
/// F2 — resolves each dispatch target's machine-concurrency policy
/// (<c>DeploymentTarget.AllowParallelTaskExecution</c>). A seam for the same reason
/// <see cref="IAdhocAgentPusher"/> is one: the dispatcher stays exercisable in unit
/// tests without a database. The DEPLOYMENT path needs no seam — its plan builder
/// already has the target entity loaded.
/// </summary>
public interface ITargetConcurrencyPolicy
{
    /// <summary>
    /// <c>targetId → AllowParallelTaskExecution</c>. Targets that no longer exist
    /// are simply absent; callers must treat "absent" as <c>false</c> (take the
    /// machine gate) — the safe default.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, bool>> GetAllowParallelAsync(
        IReadOnlyCollection<Guid> targetIds, CancellationToken ct);
}

/// <summary>
/// Production <see cref="ITargetConcurrencyPolicy"/>. Read-only and filter-free:
/// the dispatch path has no ambient Space, and target ids are globally unique so
/// this cannot cross accounts (the caller has already applied the P3-8 per-target
/// cross-account connection guard).
/// <para>
/// Singleton over <see cref="IServiceScopeFactory"/>, NOT over the context factory
/// — <c>IDbContextFactory</c> is SCOPED in this app (multi-account routing resolves
/// the tenant database per scope), so capturing it here would be the exact captive
/// dependency Dev's <c>ValidateOnBuild</c> refuses (it did, on the first boot of
/// this change). The per-read scope also rides the caller's ambient account
/// (AsyncLocal), so the lookup reads the right tenant database. Same shape as
/// <see cref="AgentCancelPusher"/>.
/// </para>
/// </summary>
public sealed class DbTargetConcurrencyPolicy(
    IServiceScopeFactory scopeFactory) : ITargetConcurrencyPolicy
{
    public async Task<IReadOnlyDictionary<Guid, bool>> GetAllowParallelAsync(
        IReadOnlyCollection<Guid> targetIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(targetIds);
        if (targetIds.Count == 0)
        {
            return new Dictionary<Guid, bool>();
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<KrakenDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.DeploymentTargets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(t => targetIds.Contains(t.Id))
            .Select(t => new { t.Id, t.AllowParallelTaskExecution })
            .ToDictionaryAsync(t => t.Id, t => t.AllowParallelTaskExecution, ct)
            .ConfigureAwait(false);
    }
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
