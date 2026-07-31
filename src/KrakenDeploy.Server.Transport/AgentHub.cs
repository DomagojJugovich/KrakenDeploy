using System.Security.Claims;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.Adhoc;
using KrakenDeploy.Server.Core.Domain.Accounts;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// SignalR hub for agent ↔ server control-plane communication.
/// Authenticated with the "AgentJwt" bearer scheme; token is delivered
/// via query string (<c>?access_token=…</c>) because WebSocket upgrades
/// cannot carry custom headers.
/// </summary>
[Authorize(AuthenticationSchemes = "AgentJwt")]
public sealed class AgentHub(
    IAgentConnectionRegistry registry,
    IDbContextFactory<KrakenDbContext> dbFactory,
    IServiceScopeFactory scopeFactory,
    TargetStatusPublisher statusPublisher,
    TimeProvider timeProvider,
    IHubContext<UiHub, IUiHubClient> uiHub,
    IPendingSubPlanRegistry subPlans,
    IPendingAdhocRegistry adhocPending,
    IAccountContext accountContext,
    KrakenDeploy.Server.Core.Domain.Variables.IEncryptionService encryption,
    KrakenDeploy.Server.Core.Domain.Audit.IAuditLog auditLog,
    ILogger<AgentHub> logger)
    : Hub<IAgentHubClient>, IAgentHubServer
{
    // E7 — how far back the reconnect reconcile looks for terminal-but-recent
    // tasks to re-cancel. A task the agent might still be executing went terminal
    // (operator cancel / reconciler interrupt) while the agent was offline; one
    // older than this is assumed no longer running on the agent (and its late
    // completion is swallowed by the terminal-status guard regardless). Anchored
    // to the wave-deadline ceiling; a hub-local constant like the 30 s
    // offline-mark grace (not a tuned operator knob). Re-pushing to a task the
    // agent is NOT running is a harmless agent-side no-op, so err generous.
    private static readonly TimeSpan ReconnectCancelReconcileWindow = TimeSpan.FromHours(1);

    // Defensive cap on the reconcile fan-out. Retention already bounds a single
    // target's terminal-task set well below this; the cap only guards against a
    // pathological history. Most-recent first, so a truncation drops the oldest.
    private const int ReconnectCancelReconcileCap = 100;

    // ── Connection lifecycle ────────────────────────────────────────────────

    public override async Task OnConnectedAsync()
    {
        var targetId = GetTargetId();
        if (targetId is null)
        {
            logger.LogWarning(
                "Agent connected (conn {ConnectionId}) with no valid NameIdentifier claim; aborting.",
                Context.ConnectionId);
            Context.Abort();
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        // The target id is the authenticated agent's own NameIdentifier; load it
        // filter-free since the hub has no ambient Space and the global filter
        // would otherwise hide a target that lives in a non-Default Space. In
        // multi-account mode AgentAccountHubFilter has already pinned this
        // connection's account, so this reads the correct tenant database.
        var target = await db.DeploymentTargets
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == targetId.Value)
            .ConfigureAwait(false);

        if (target is null)
        {
            // Fail closed: a connection whose target does not exist is rejected, not
            // left registered. It is either a stale credential (target deleted) or —
            // in multi-account — an agent that reached the wrong account: the target
            // id is globally unique, so a foreign target simply is not in this
            // account's database. (Pre-P3-8 this only logged and stayed connected.)
            logger.LogWarning(
                "Target {TargetId} connected (conn {ConnectionId}) but was not found; aborting.",
                targetId.Value, Context.ConnectionId);
            Context.Abort();
            return;
        }

        if (target.IsRetired)
        {
            // A retired (soft-decommissioned) target must not reconnect: it is hidden
            // from matching/dispatch and its history is preserved, so an agent that
            // keeps trying to check in is refused here rather than marked Online.
            logger.LogWarning(
                "Retired target {TargetId} connected (conn {ConnectionId}); aborting.",
                targetId.Value, Context.ConnectionId);
            Context.Abort();
            return;
        }

        // Record the connection's account (host-derived, pinned by AgentAccountHubFilter
        // before this runs; Guid.Empty single-instance) so dispatch can assert a target's
        // live connection belongs to the dispatching account (P3-8 Phase 5).
        var accountId = accountContext.IsResolved ? accountContext.CurrentAccountId : Guid.Empty;

        target.Status = TargetStatus.Online;
        target.LastSeenUtc = timeProvider.GetUtcNow();
        await db.SaveChangesAsync().ConfigureAwait(false);

        logger.LogInformation(
            "Target {TargetId} connected (conn {ConnectionId}); marked Online.",
            targetId.Value, Context.ConnectionId);

        await statusPublisher
            .PublishAsync(targetId.Value, TargetStatus.Online, target.LastSeenUtc, accountId)
            .ConfigureAwait(false);

        // REGISTER LAST, and this ordering is load-bearing rather than tidy. SignalR
        // deliberately skips OnDisconnectedAsync when OnConnectedAsync fails, so ANYTHING that
        // can throw after this line leaks a registry entry that nothing will ever remove — and
        // since the entry is what makes a target dispatchable, the leak is not inert: the wave
        // dispatches to a dead connection id (Clients.Client(deadId) is a silent no-op),
        // HasConnectionFor reads true so B3's disconnect monitor never diagnoses it, and the
        // wave hangs to its deadline while the fleet page shows the target green. Both writes
        // above can throw (a saturated tenant DB; an in-process status subscriber), which is
        // exactly the shape that produced it. Keep this the last statement, and if something
        // must follow it, it has to remove the entry on the way out.
        registry.Add(
            Context.ConnectionId,
            targetId.Value,
            accountId,
            // A8/T1-12: lets a token revocation drop this live tunnel immediately.
            Context.Abort);

        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (registry.TryRemove(Context.ConnectionId, out var targetId))
        {
            if (exception is not null)
            {
                logger.LogWarning(
                    exception,
                    "Target {TargetId} disconnected with error; scheduling offline mark.",
                    targetId);
            }
            else
            {
                logger.LogInformation(
                    "Target {TargetId} disconnected cleanly; scheduling offline mark.",
                    targetId);
            }

            // Capture the account now, while AgentAccountHubFilter still has it pinned
            // (Guid.Empty single-instance) — the deferred task needs it to scope the UI
            // push to this tenant's group. The task's tenant DB write rides the same
            // account via the ambient AsyncLocal captured into its ExecutionContext.
            var accountId = accountContext.IsResolved ? accountContext.CurrentAccountId : Guid.Empty;

            // Fire-and-forget with 30 s grace period so brief reconnects don't flicker.
            _ = MarkOfflineAfterGraceAsync(
                targetId, accountId, scopeFactory, registry, statusPublisher, timeProvider, logger);
        }

        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    // ── IAgentHubServer implementation ─────────────────────────────────────

    /// <summary>
    /// Records the machine's self-reported details, and re-pushes cooperative cancels for
    /// tasks that went terminal while the agent was away. Both best-effort; neither is a GATE
    /// on anything. The wire contract is verified on the handshake and the target is resolved
    /// in <see cref="OnConnectedAsync"/>, so a connection that reaches this method is already
    /// dispatchable — a throw here means only "machine info was not recorded this cycle",
    /// which the next reconnect or heartbeat corrects. No try/catch and no abort: SignalR
    /// faults the invocation, the agent logs it, and the connection stays up and usable.
    /// (The version that aborted the connection to force a retry was harmful twice over —
    /// <c>Context.Abort()</c> drops the transport rather than closing it, and removing the
    /// registry entry alongside it suppressed the target's offline mark.)
    /// </summary>
    public async Task<AgentRegistrationResult> RegisterAsync(AgentRegistrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var targetId = GetTargetId();
        if (targetId is null)
        {
            return new AgentRegistrationResult(
                Accepted: false, AgentContract.CurrentVersion, "No valid agent identity.");
        }

        await using var db = await dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        // The target id is the authenticated agent's own NameIdentifier; load it
        // filter-free since the hub has no ambient Space and the global filter
        // would otherwise hide a target that lives in a non-Default Space.
        var target = await db.DeploymentTargets
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == targetId.Value)
            .ConfigureAwait(false);

        if (target is null)
        {
            return new AgentRegistrationResult(
                Accepted: false, AgentContract.CurrentVersion, "Unknown target.");
        }

        if (target.IsRetired)
        {
            // Refuse a retired (soft-decommissioned) target's registration: it is
            // hidden from matching/dispatch and its history preserved. Remove the
            // connection from the dispatch registry (undispatchable NOW). Keep the
            // status Disabled (as TargetService.RetireAsync set it) rather than
            // downgrading to Offline, so the fleet summary still reflects the
            // decommissioned state. Only write if the status actually changed
            // (avoids a redundant DB write on every zombie-agent reconnect).
            registry.TryRemove(Context.ConnectionId, out _);
            if (target.Status != TargetStatus.Disabled)
            {
                target.Status = TargetStatus.Disabled;
                await db.SaveChangesAsync().ConfigureAwait(false);
            }

            const string message =
                "This target has been retired and can no longer connect. " +
                "Re-enroll a new target if the host is being recommissioned.";
            logger.LogWarning(
                "Retired target {TargetId} (agent {AgentVersion}) REFUSED registration.",
                targetId.Value, request.AgentVersion);
            await auditLog.RecordAsync(
                KrakenDeploy.Server.Core.Domain.Audit.AuditEventType.AgentRetiredTargetRejected,
                subjectType: "DeploymentTarget",
                subjectId:   targetId.Value.ToString(),
                subjectName: target.Name,
                details:     "Registration refused: target is retired.").ConfigureAwait(false);

            var retiredAccountId = accountContext.IsResolved ? accountContext.CurrentAccountId : Guid.Empty;
            await statusPublisher
                .PublishAsync(targetId.Value, TargetStatus.Disabled, target.LastSeenUtc, retiredAccountId)
                .ConfigureAwait(false);

            return new AgentRegistrationResult(
                Accepted: false, AgentContract.CurrentVersion, message);
        }

        // NO contract-version gate here. It moved onto the SignalR handshake
        // (AgentContractHandshakeGate), which refuses a skewed agent with 426 before the
        // connection is admitted — so by the time any hub method runs, the version is
        // already verified. A second, later gate would reintroduce the
        // connected-but-unverified window that change exists to delete.
        //
        // But the body field is still on the wire, and comparing it here is a free
        // TRIPWIRE for the one risk the move introduced: enforcement now rides a request
        // HEADER, and a header-whitelisting intermediary would STRIP it, at which point the
        // gate admits every agent silently. The precedent cited for header safety
        // (X-KD-Release) does not transfer — that header is optional, so its working has
        // never proved the path preserves headers. If these two disagree, the header did not
        // arrive as sent and the gate is not enforcing anything.
        if (request.ContractVersion != AgentContract.CurrentVersion)
        {
            logger.LogError(
                "Target {TargetId} registered with body contract v{Body} but the handshake gate " +
                "admitted it as v{Required}. The {Header} header did not reach the server as " +
                "sent — an intermediary is stripping or rewriting it, and the wire-contract " +
                "gate is currently enforcing NOTHING. Check the proxy chain.",
                targetId.Value, request.ContractVersion, AgentContract.CurrentVersion,
                AgentContract.VersionHeader);
        }

        // E7 — reconcile in-flight cancellations on (re)connect, BEFORE the machine-info
        // write. An agent offline when its task was cancelled/interrupted keeps executing
        // that task to completion (a disconnect never aborts a running step); the original
        // cancel push skipped it (no live connection) and nothing else reconciles on
        // reconnect. Re-push a cooperative cancel — straight to THIS connection — for this
        // target's terminal-but-recent tasks so the running step's process tree dies.
        //
        // The ORDER matters and used to be the other way round. This is the only call site,
        // and nothing else re-runs it: HeartbeatAsync repairs machine info every 30 s but
        // never re-invokes registration, and a healthy link produces no reconnect. So a
        // SaveChangesAsync failure below — the shape this method is explicitly documented to
        // tolerate — used to skip the reconcile with no retry path, and a task the operator
        // was told is cancelled ran its step to completion on a production box. The reconcile
        // is keyed only on (targetId, connectionId), documented idempotent, and never throws,
        // so running it first costs nothing.
        //
        // Awaited (a single lookup + direct sends, no fan-out) so the push is issued before we
        // return and no detached task lingers — safe, because the server→client cancel send
        // does not block on the agent's pending RegisterAsync round-trip.
        await ReconcileTerminalTasksForReconnectAsync(targetId.Value, Context.ConnectionId)
            .ConfigureAwait(false);

        // T1-7 / B6: an agent reports machine capabilities only. Authorization
        // roles drive secret scoping (VariableScope.Matches resolves against the
        // target's CURRENT roles at dispatch), so they are OPERATOR-assigned via
        // the registration wizard / target-edit UI / API — the wire field was
        // removed in the B6 contract pass, so there is nothing to ignore anymore.
        target.MachineName = request.MachineName;
        target.OperatingSystem = request.OperatingSystem;
        target.AgentVersion = request.AgentVersion;

        target.Status = TargetStatus.Online;
        target.LastSeenUtc = timeProvider.GetUtcNow();

        await db.SaveChangesAsync().ConfigureAwait(false);

        logger.LogInformation(
            "Target {TargetId} registered: machine={Machine}, OS={OS}, agent={Version}, contract=v{Contract}.",
            targetId.Value,
            request.MachineName,
            request.OperatingSystem,
            request.AgentVersion,
            request.ContractVersion);

        return new AgentRegistrationResult(Accepted: true, AgentContract.CurrentVersion);
    }

    /// <summary>
    /// E7 — on (re)connect, re-push a cooperative cancel to THIS connection for
    /// every task assigned to <paramref name="targetId"/> whose DB status is
    /// terminal (<see cref="DeploymentStatus.Cancelled"/> /
    /// <see cref="DeploymentStatus.Failed"/>) within
    /// <see cref="ReconnectCancelReconcileWindow"/> — the ones the agent may still
    /// be running because it was offline when the verdict was recorded and a
    /// disconnected step runs to completion. The original cancel push skipped the
    /// target then (no live connection); this closes the gap with a pure
    /// SERVER-SIDE lookup (no wire change — the agent does not report its in-flight
    /// ids). Best-effort by contract: never throws, so a failure here cannot fail
    /// registration, and the agent's late completion is swallowed by the
    /// terminal-status guard regardless.
    /// <para>
    /// The push goes straight to <paramref name="connectionId"/> (the reconnecting
    /// connection), NOT through the fan-out <c>AgentCancelPusher</c> — that would
    /// re-query each task's full target set and notify every assigned target, an
    /// N+1 + cross-target cancel storm amplified across a whole fleet reconnecting
    /// after a restart. Only this target may still be running these tasks after
    /// ITS offline window; the other targets reconcile on their own reconnect.
    /// </para>
    /// <para>
    /// <c>internal</c> so <c>KrakenDeploy.Server.Data.Tests</c> can drive it
    /// deterministically (InternalsVisibleTo), like the worker's other test seams.
    /// Self-contained (its own DbContext + DI scope) and rides the caller's ambient
    /// account (AsyncLocal), so the target lookup and the push both hit the right
    /// tenant.
    /// </para>
    /// </summary>
    internal async Task ReconcileTerminalTasksForReconnectAsync(
        Guid targetId, string connectionId, CancellationToken ct = default)
    {
        try
        {
            var cutoff = timeProvider.GetUtcNow() - ReconnectCancelReconcileWindow;

            List<Guid> taskIds;
            await using (var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
            {
                // Filter-free: the hub has no ambient Space; the assignment join is
                // the authority for "which tasks hit this target" and target ids are
                // globally unique, so this cannot cross accounts.
                taskIds = await db.TaskTargetAssignments
                    .IgnoreQueryFilters()
                    .Where(a => a.TargetId == targetId)
                    .Join(
                        db.ServerTasks.IgnoreQueryFilters(),
                        a => a.TaskId,
                        t => t.Id,
                        (a, t) => t)
                    .Where(t =>
                        (t.Status == DeploymentStatus.Cancelled
                            || t.Status == DeploymentStatus.Failed)
                        && t.CompletedUtc != null
                        && t.CompletedUtc >= cutoff)
                    .OrderByDescending(t => t.CompletedUtc)
                    .Select(t => t.Id)
                    .Take(ReconnectCancelReconcileCap)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
            }

            if (taskIds.Count == 0)
            {
                return;
            }

            // Push straight to the reconnecting connection. IHubContext is a
            // singleton resolved from a fresh scope; the send routes by connection
            // id and is a harmless no-op if the connection has since dropped. A
            // cancel for a task the agent is not actually running is likewise a
            // harmless agent-side no-op (TryCancel finds no in-flight run).
            await using var scope = scopeFactory.CreateAsyncScope();
            var hub = scope.ServiceProvider
                .GetRequiredService<IHubContext<AgentHub, IAgentHubClient>>();
            var client = hub.Clients.Client(connectionId);
            foreach (var taskId in taskIds)
            {
                await client.CancelDeploymentAsync(
                    taskId,
                    "Task reached a terminal status while the agent was offline; " +
                    "re-pushing cooperative cancel on reconnect.")
                    .ConfigureAwait(false);
            }

            logger.LogInformation(
                "Reconnect reconcile for target {TargetId}: re-pushed cancel for {Count} " +
                "terminal-but-recent task(s).",
                targetId, taskIds.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Reconnect cancel-reconcile for target {TargetId} failed; the agent's late " +
                "completion is swallowed by the terminal-status guard regardless.", targetId);
        }
    }

    public async Task HeartbeatAsync(HeartbeatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var targetId = GetTargetId();
        if (targetId is null)
        {
            return;
        }

        // E4 backstop: re-affirm the target→connection mapping so a mapping wiped
        // by a late, out-of-order disconnect of a superseded connection self-heals
        // within one heartbeat. Pure in-memory and idempotent; kept before the DB
        // read so healing does not depend on the target row loading.
        var heartbeatAccountId = accountContext.IsResolved
            ? accountContext.CurrentAccountId
            : Guid.Empty;
        registry.Reaffirm(Context.ConnectionId, targetId.Value, heartbeatAccountId, Context.Abort);

        await using var db = await dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        // The target id is the authenticated agent's own NameIdentifier; load it
        // filter-free since the hub has no ambient Space and the global filter
        // would otherwise hide a target that lives in a non-Default Space.
        var target = await db.DeploymentTargets
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == targetId.Value)
            .ConfigureAwait(false);

        if (target is null)
        {
            return;
        }

        target.LastSeenUtc = timeProvider.GetUtcNow();
        if (request.MachineName is not null) { target.MachineName = request.MachineName; }
        if (request.OperatingSystem is not null) { target.OperatingSystem = request.OperatingSystem; }
        if (request.AgentVersion is not null) { target.AgentVersion = request.AgentVersion; }

        await db.SaveChangesAsync().ConfigureAwait(false);
        logger.LogDebug("Heartbeat from target {TargetId}.", targetId.Value);
    }

    public Task ReportStatusAsync(string status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var targetId = GetTargetId();
        logger.LogInformation(
            "Status report from target {TargetId}: {Status}.", targetId, status);
        return Task.CompletedTask;
    }

    public async Task AppendLogAsync(
        Guid deploymentId, Guid dispatchId, int stepIndex, string level, string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // B6: agent-supplied stepIndex sanity — it is persisted and used for
        // per-step log grouping; -1 is the plan-level sentinel, anything below
        // is malformed. (It is never used as an array index on this path, but
        // clamping keeps the compactor's grouping keys sane.)
        if (stepIndex < -1)
        {
            stepIndex = -1;
        }

        // B6: drop lines from a dispatch attempt the registry has POSITIVELY
        // retired (superseded / timed-out wave attempt still flushing its
        // outbox) — an abandoned attempt must not interleave noise into the
        // current attempt's log. Guid.Empty (legacy/offline) and unknown ids
        // (post-restart the retired set is empty) are always accepted.
        if (subPlans.IsRetiredDispatch(dispatchId))
        {
            logger.LogDebug(
                "AppendLog for task {Id} dropped: dispatch {Dispatch} is retired.",
                deploymentId, dispatchId);
            return;
        }

        var connectionTargetId = GetTargetId();
        var timestamp = timeProvider.GetUtcNow();

        await using var db = await dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        // ONE lookup — deployments and runbook runs share the server_tasks spine
        // (no more Deployment-then-RunbookRun probe). Filter-free: the hub has no
        // ambient Space (DefaultSpaceId); ownership is enforced via the join below.
        var task = await db.ServerTasks
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == deploymentId)
            .ConfigureAwait(false);
        if (task is null)
        {
            logger.LogWarning("AppendLog for unknown task {Id}; ignored.", deploymentId);
            return;
        }

        if (connectionTargetId is null
            || !await AgentDeploymentOwnership.ConnectionOwnsTaskAsync(
                db, task, connectionTargetId.Value).ConfigureAwait(false))
        {
            logger.LogWarning(
                "AppendLog rejected: target {Target} is not assigned to task {Id}.",
                connectionTargetId, deploymentId);
            return;
        }

        // DB-atomic sequence + staging insert (shared with the server-side path).
        var seq = await TaskLogService.AppendLiveAsync(
            db, deploymentId, stepIndex, connectionTargetId, level, message, timestamp)
            .ConfigureAwait(false);

        await uiHub.Clients.Group($"deployment:{deploymentId}")
            .DeploymentLogAppendedAsync(deploymentId, seq, timestamp, level, message)
            .ConfigureAwait(false);
    }

    public async Task CompleteDeploymentAsync(
        Guid deploymentId, Guid dispatchId, bool success, string? errorMessage)
    {
        // If this is a sub-plan completion (the worker is mid-orchestration
        // for a multi-target or mixed-side process), resolve the pending TCS
        // keyed by (deploymentId, this connection's target id) and stop here —
        // DeploymentWorker will finalize the deployment status once every
        // wave / target has completed.
        //
        // M-RollingDeployments Phase 1b: slot key is (deployment, target);
        // the target id comes from the connection's NameIdentifier claim.
        // B2 (B6.2): dispatchId pins the completion to the attempt that
        // produced it — the agent's at-least-once report outbox may deliver a
        // completion late (after the wave was cancelled/re-dispatched) or
        // twice (ack lost in a disconnect). Neither may resolve a DIFFERENT
        // attempt's TCS, and neither may fall through to the DB fallback
        // below, which would finalize a mid-flight deployment.
        var connectionTargetId = GetTargetId();
        if (connectionTargetId is not null)
        {
            var route = subPlans.RouteCompletion(
                deploymentId, connectionTargetId.Value, dispatchId,
                new SubPlanResult(success, errorMessage));

            if (route == SubPlanCompletionRoute.ResolvedPending)
            {
                logger.LogDebug(
                    "Sub-plan complete for deployment {Id} target {Target} dispatch {Dispatch} " +
                    "(success={Success}); orchestrator will continue.",
                    deploymentId, connectionTargetId.Value, dispatchId, success);
                return;
            }

            if (route == SubPlanCompletionRoute.StaleOrDuplicate)
            {
                logger.LogWarning(
                    "Stale or duplicate completion for deployment {Id} target {Target} " +
                    "dispatch {Dispatch} (success={Success}); swallowed.",
                    deploymentId, connectionTargetId.Value, dispatchId, success);
                return;
            }
        }

        await using var db = await dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        // ONE lookup on the unified spine — reached only for a completion no open
        // orchestrator slot claimed. The lookup + ownership check below exist for
        // the log signal (unknown task vs foreign agent vs orphaned completion);
        // nothing state-changing happens on this path any more.
        var task = await db.ServerTasks
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == deploymentId)
            .ConfigureAwait(false);
        if (task is null)
        {
            logger.LogWarning("CompleteDeployment for unknown task {Id}; ignored.", deploymentId);
            return;
        }

        if (connectionTargetId is null
            || !await AgentDeploymentOwnership.ConnectionOwnsTaskAsync(
                db, task, connectionTargetId.Value).ConfigureAwait(false))
        {
            logger.LogWarning(
                "CompleteDeployment rejected: target {Target} is not assigned to task {Id}.",
                connectionTargetId, deploymentId);
            return;
        }

        // E1 / D1 Phase 3: the hub NEVER finalizes a task — either kind is
        // finalized by its orchestrator through the sub-plan registry above. A
        // completion reaching this point has no open orchestrator slot: either a
        // late/buffered attempt the registry already retired, or the dangerous
        // interleaving — the server restarted mid-task faster than the lease, the
        // in-memory wave state died with the old process, and the agent's
        // buffered WAVE completion flushed into the FRESH process. Finalizing
        // here would mark the WHOLE task terminal although its remaining waves
        // never ran. Drop it: the reconciler owns a genuinely-orphaned task
        // (fails it once its lease expires), and a live orchestrator finalizes
        // through the registry, not here. (The transition-era fallback that
        // finalized LEGACY pre-D1 hand-off runbook runs — Running with a
        // released lease — was deleted in D1 Phase 3 together with reconciler
        // arm 4.)
        logger.LogWarning(
            "CompleteDeployment for {Kind} {Id} (dispatch {Dispatch}, success={Success}{Error}) " +
            "reached the hub with no open orchestrator slot; dropping. Tasks are finalized " +
            "by their orchestrator, never by the hub — a buffered wave completion arriving " +
            "post-restart must not finalize the whole task.",
            task.Kind, deploymentId, dispatchId, success,
            errorMessage is null ? "" : $", error: {errorMessage}");
    }

    /// <summary>
    /// F2 — the agent reports that this dispatch attempt has ACQUIRED its machine
    /// execution gate and is executing now. The orchestrator re-arms the wave
    /// deadline from here (the dispatch-time arm is only the backstop ceiling), so a
    /// sub-plan queued behind a long-running task on the same box does not burn its
    /// budget while waiting.
    /// <para>
    /// Authorization needs no DB round-trip on this path, unlike the log / step /
    /// completion reports: the slot is looked up by (task id, THIS connection's
    /// claimed target id) and the attempt's server-generated
    /// <see cref="KrakenDeploy.Contracts.DeploymentPlan.DispatchId"/> must match
    /// exactly. A foreign agent therefore probes its OWN (empty) slot and gets a
    /// no-op — it cannot reach another target's wave even knowing the task id.
    /// </para>
    /// <para>
    /// Purely advisory: an unmatched report (retired attempt, unknown task,
    /// post-restart, duplicate outbox delivery) is a silent no-op, and no report ever
    /// touches a task's verdict. What a MATCHED report does is normally SHORTEN the
    /// attempt's remaining time — it swaps the backstop ceiling (execution budget +
    /// queue-wait ceiling) for the execution budget alone. Three guards bound what an
    /// agent can buy for itself by reporting: the dispatch id must match the live
    /// attempt exactly, the registry's interlock makes it one-shot per attempt, and
    /// the re-arm is CLAMPED to the backstop instant (F2-followup 6), so reporting
    /// late cannot stack another full budget on top of the queue wait. Total time is
    /// therefore capped at the backstop no matter when — or whether — the report
    /// arrives.
    /// </para>
    /// </summary>
    public Task ReportExecutionStartedAsync(Guid deploymentId, Guid dispatchId)
    {
        var connectionTargetId = GetTargetId();
        if (connectionTargetId is null)
        {
            logger.LogWarning(
                "ReportExecutionStarted from connection {ConnectionId} with no valid " +
                "NameIdentifier claim; dropping.", Context.ConnectionId);
            return Task.CompletedTask;
        }

        if (subPlans.TryMarkExecutionStarted(deploymentId, connectionTargetId.Value, dispatchId))
        {
            logger.LogDebug(
                "Task {Id} target {Target} dispatch {Dispatch} started executing on the agent; " +
                "wave deadline re-armed from gate acquisition.",
                deploymentId, connectionTargetId.Value, dispatchId);
        }
        else
        {
            logger.LogDebug(
                "ReportExecutionStarted for task {Id} target {Target} dispatch {Dispatch} " +
                "matched no open attempt (retired, duplicate or unknown); ignored.",
                deploymentId, connectionTargetId.Value, dispatchId);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// M14.4 — per-step boundary callback from the agent. Persists captured
    /// output variables (same upsert as the pre-M14.4 path) AND records the
    /// per-step outcome in <see cref="IPendingSubPlanRegistry"/> so
    /// <see cref="DeploymentWorker"/> can attribute Required failures to
    /// the actual failing step inside a parallel wave.
    ///
    /// <para>
    /// The DB shape is unchanged: <c>DeploymentOutputVariable</c> rows
    /// keyed by (DeploymentId, StepName, Name). Per-step attribution
    /// lives in the registry's in-memory bag rather than a new table —
    /// per-step state is only useful within the wave window, the audit
    /// log already captures forensic detail (Required/non-required
    /// failures, retries, timeouts), and persisting a per-step status
    /// column would add migration churn without operator benefit.
    /// </para>
    /// </summary>
    public async Task ReportStepCompletedAsync(
        Guid deploymentId,
        Guid dispatchId,
        int stepIndex,
        string stepName,
        bool success,
        string? errorMessage,
        Dictionary<string, string> outputVariables,
        List<string> sensitiveOutputNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
        ArgumentNullException.ThrowIfNull(outputVariables);
        // Older agents / offline JSON may omit the sensitive-name list.
        sensitiveOutputNames ??= [];

        // B6: bounds-check the agent-supplied step index at the trust boundary.
        // The orchestrator resolves it against the plan's StepSnapshot array —
        // an out-of-range value (int.MaxValue from a buggy/malicious agent)
        // would throw inside the wave fold and abort the whole cross-target
        // deployment. -1 is not meaningful here either: step reports are
        // always step-scoped.
        if (stepIndex < 0)
        {
            logger.LogWarning(
                "ReportStepCompleted for task {Id} rejected: step index {Index} is out of range.",
                deploymentId, stepIndex);
            return;
        }

        // Register the per-step outcome with the sub-plan registry FIRST so
        // even if DB persistence fails, the orchestrator gets attribution
        // for the wave's per-step Required gate. Late reports for waves
        // that already resolved — and, B2, stale reports whose dispatchId
        // belongs to a previous attempt of a re-dispatched wave — are
        // dropped silently inside RecordStepResult.
        //
        // M-RollingDeployments Phase 1b: slot key is (deployment, this
        // connection's target id). The target id comes from the connection's
        // NameIdentifier claim.
        var connectionTargetId = GetTargetId();
        if (connectionTargetId is not null)
        {
            subPlans.RecordStepResult(
                deploymentId, connectionTargetId.Value, dispatchId,
                new SubPlanStepResult(
                    StepIndex:    stepIndex,
                    StepName:     stepName,
                    Success:      success,
                    ErrorMessage: errorMessage,
                    Outputs:      new Dictionary<string, string>(
                                      outputVariables, StringComparer.OrdinalIgnoreCase),
                    // B4: sensitivity rides into the registry so the online
                    // output merge can mask these in later waves' plans.
                    SensitiveOutputNames: sensitiveOutputNames));
        }

        // E-C: mirror AppendLogAsync's retired-dispatch guard onto the DB half.
        // RecordStepResult above already drops stale/retired attempts in memory
        // (its _pending slot no longer matches), but the DB persistence below is
        // dispatch-agnostic: TaskOutputVariableStore.UpsertAsync keys on
        // (task, step, name) with no dispatch dimension, so a retired attempt's
        // late report — flushed from the B2 outbox after the wave was
        // superseded/re-dispatched — would OVERWRITE the CURRENT attempt's output
        // variables, and TaskLogService.CompactStepAsync would prematurely fold
        // the current attempt's staged step lines mid-step. Only POSITIVE
        // retirement drops the report; Guid.Empty (legacy/offline) and unknown
        // ids (post-restart the retired set is empty) fall through, exactly as
        // on AppendLogAsync.
        if (subPlans.IsRetiredDispatch(dispatchId))
        {
            logger.LogDebug(
                "ReportStepCompleted for task {Id} dropped: dispatch {Dispatch} is retired.",
                deploymentId, dispatchId);
            return;
        }

        var capturedAt = timeProvider.GetUtcNow();

        await using var db = await dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        // ONE lookup on the unified spine — output variables are now persisted for
        // runbook runs too (the pre-unification drop is fixed). Filter-free; stamp
        // the child rows' SpaceId from the loaded task.
        var scope = await db.ServerTasks.IgnoreQueryFilters()
            .Where(t => t.Id == deploymentId)
            .Select(t => new { t.SpaceId })
            .FirstOrDefaultAsync().ConfigureAwait(false);
        if (scope is null)
        {
            logger.LogDebug(
                "ReportStepCompleted for unknown task {Id}; ignored.", deploymentId);
            return;
        }

        // Ownership: only a target assigned to this task may persist its outputs
        // or compact its logs — otherwise a foreign agent could inject outputs that
        // later steps consume.
        if (connectionTargetId is null
            || !await AgentDeploymentOwnership
                   .ConnectionOwnsTaskAsync(db, deploymentId, connectionTargetId.Value)
                   .ConfigureAwait(false))
        {
            logger.LogWarning(
                "ReportStepCompleted rejected: target {Target} is not assigned to task {Id}.",
                connectionTargetId, deploymentId);
            return;
        }

        // B4: single upsert path shared with the server-side capture fold
        // (DeploymentWorker / ServerScriptStepRunner) — same encryption rules.
        await TaskOutputVariableStore.UpsertAsync(
            db, deploymentId, scope.SpaceId, stepName,
            outputVariables, sensitiveOutputNames, capturedAt, encryption)
            .ConfigureAwait(false);

        // Step-completion compaction (decision 3): fold this (task, step, target)'s
        // staging log lines into a single blob. No-op when the step logged nothing.
        await TaskLogService.CompactStepAsync(
            db, deploymentId, stepIndex, connectionTargetId, capturedAt).ConfigureAwait(false);

        logger.LogInformation(
            "Step '{Step}' (index {Index}) of task {Id} completed: " +
            "success={Success}, outputs={Count}.",
            stepName, stepIndex, deploymentId, success, outputVariables.Count);
    }

    /// <summary>
    /// M11.E.7 — the agent's per-target adhoc-script outcome callback. The
    /// hub resolves the connection's target id from its NameIdentifier claim
    /// and routes the result to the matching slot in
    /// <see cref="IPendingAdhocRegistry"/>. Late reports for a slot that has
    /// already been cancelled (timeout / cleanup) are dropped silently — the
    /// dispatcher's TCS has already resolved with an AgentError.
    /// </summary>
    public Task ReportAdhocResultAsync(AdhocScriptResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var connectionTargetId = GetTargetId();
        if (connectionTargetId is null)
        {
            logger.LogWarning(
                "ReportAdhocResult from connection {ConnectionId} with no valid " +
                "NameIdentifier claim; dropping.", Context.ConnectionId);
            return Task.CompletedTask;
        }

        if (!adhocPending.TryResolve(
                result.SessionId, result.IterNumber, connectionTargetId.Value, result))
        {
            logger.LogDebug(
                "ReportAdhocResult for session {SessionId} iter {Iter} target {Target} " +
                "arrived after the slot was cancelled / unknown; dropping.",
                result.SessionId, result.IterNumber, connectionTargetId.Value);
        }
        return Task.CompletedTask;
    }

    // D1 Phase 3: the hub's PruneRetentionAsync is gone with the fallback
    // finalize — retention fires from DeploymentWorker for both kinds.

    // ── Helpers ────────────────────────────────────────────────────────────

    private Guid? GetTargetId()
    {
        var claim = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    private static async Task MarkOfflineAfterGraceAsync(
        Guid targetId,
        Guid accountId,
        IServiceScopeFactory scopeFactory,
        IAgentConnectionRegistry registry,
        TargetStatusPublisher statusPublisher,
        TimeProvider timeProvider,
        ILogger logger)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

            // If the agent reconnected during the grace period, do nothing.
            if (registry.HasConnectionFor(targetId))
            {
                return;
            }

            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();

            // Filter-free: own-target by-id read in a Space-less background scope.
            var target = await db.DeploymentTargets
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.Id == targetId)
                .ConfigureAwait(false);

            if (target is null || target.Status != TargetStatus.Online)
            {
                return;
            }

            target.Status = TargetStatus.Offline;
            target.LastSeenUtc = timeProvider.GetUtcNow();
            await db.SaveChangesAsync().ConfigureAwait(false);

            logger.LogInformation(
                "Target {TargetId} marked Offline after 30 s grace period.", targetId);

            await statusPublisher
                .PublishAsync(targetId, TargetStatus.Offline, target.LastSeenUtc, accountId)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Unhandled error in grace-period offline task for target {TargetId}.", targetId);
        }
    }
}
