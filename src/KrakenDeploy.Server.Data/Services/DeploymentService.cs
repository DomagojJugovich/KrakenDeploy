using System.Threading.Channels;
using KrakenDeploy.Server.Core.Domain.Accounts;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Creates deployments and enqueues them for dispatch to the target agent.
/// </summary>
public class DeploymentService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    Channel<TenantWorkItem> deploymentQueue,
    TimeProvider time,
    IAccountContext accountContext,
    IPermissionEvaluator permissions,
    // B6: optional — registered in the server host; tests that construct the
    // service directly (and the CLI) skip the agent push and keep the
    // wave-boundary cancel semantics.
    IAgentCancelPusher? cancelPusher = null,
    // Optional (same host-registered / tests-skip pattern as cancelPusher):
    // CancelAsync records the semantic Deployment.Cancelled audit itself so no
    // cancel surface can omit it. Null in tests → only the interceptor's
    // "Deployment.Updated" row is written, which no test asserts on.
    IAuditLog? auditLog = null)
{
    // ── Create ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="Deployment"/> in the <c>Queued</c> state and hands it
    /// to the <see cref="DeploymentWorker"/> via the in-process channel.
    /// When <paramref name="scheduledFor"/> is a future timestamp the deployment
    /// is persisted but NOT dispatched — the Hangfire
    /// <c>ScheduledDeploymentDispatchJob</c> picks it up when the time arrives.
    /// Enforces the lifecycle gate if the release has a channel with a lifecycle.
    ///
    /// <para>
    /// The target set is the union of <paramref name="targetId"/> (the
    /// primary — always first) and <paramref name="additionalTargetIds"/>,
    /// persisted exclusively as <c>deployment_target_assignments</c> rows
    /// (the transitional <c>deployments.target_id</c> column is gone). The
    /// primary is the canonical target: server waves resolve machine
    /// variables against the first-assigned target. Pass <c>null</c> or an
    /// empty list (the default) for single-target deployments.
    /// </para>
    /// </summary>
    public async Task<Deployment> CreateAsync(
        Guid releaseId,
        Guid environmentId,
        Guid targetId,
        TaskInitiator initiator,
        CallerAuthorization caller,
        Guid? tenantId = null,
        DateTimeOffset? scheduledFor = null,
        IReadOnlyCollection<Guid>? additionalTargetIds = null,
        DeploymentFailureMode failureMode = DeploymentFailureMode.BestEffort,
        // E3: set for a child deployment spawned by an Octopus.DeployRelease step.
        // Stamped on the row BEFORE the dispatch wake-up is enqueued, so the
        // worker's gate-bypass read (children don't take a NodeTaskGate slot)
        // sees it reliably — the pre-fix "set ParentTaskId after CreateAsync in a
        // second scope" left a window where the child could dispatch before the
        // link committed.
        Guid? parentTaskId = null,
        IReadOnlyDictionary<string, string>? promptedValues = null,
        CancellationToken ct = default)
    {
        // Guard: reject a default/unset initiator before we do any work.
        initiator.EnsureValid();
        ArgumentNullException.ThrowIfNull(caller);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Load the release's denormalized ownership (project + channel + Space) to
        // stamp onto the task at creation (decision 5), and validate it exists.
        var release = await db.Releases
            .Where(r => r.Id == releaseId)
            .Select(r => new { r.ProjectId, r.ChannelId, r.SpaceId })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Release {releaseId} not found.");

        // T1-8: authoritative sub-Space authorization — deploying to THIS
        // project + environment (+ tenant). Strict, so an Environment=Test-scoped
        // DeploymentCreate grant can't deploy to Prod. Runs for every surface
        // (REST/CLI/MCP); system-initiated calls (parent DeployRelease step) skip
        // it. Checked before any existence probe so a forbidden caller learns
        // nothing about the environment/tenant.
        await permissions.EnsureScopedAsync(
            caller, Permission.DeploymentCreate,
            new PermissionScope(
                SpaceId:       release.SpaceId,
                ProjectId:     release.ProjectId,
                EnvironmentId: environmentId,
                TenantId:      tenantId),
            ct).ConfigureAwait(false);

        var envExists = await db.Environments.AnyAsync(e => e.Id == environmentId, ct)
            .ConfigureAwait(false);
        if (!envExists)
        {
            throw new InvalidOperationException($"Environment {environmentId} not found.");
        }

        if (tenantId.HasValue)
        {
            var tenantExists = await db.Tenants.AnyAsync(t => t.Id == tenantId.Value, ct)
                .ConfigureAwait(false);
            if (!tenantExists)
            {
                throw new InvalidOperationException($"Tenant {tenantId.Value} not found.");
            }
        }

        // ── Build the target id set ─────────────────────────────────────
        // Primary targetId is always part of the set (the first join row).
        // Additional ids extend it; duplicates are de-duplicated. Distinct
        // against the primary so adding it twice is a no-op.
        var targetIds = new List<Guid> { targetId };
        if (additionalTargetIds is not null)
        {
            foreach (var id in additionalTargetIds)
            {
                if (id != targetId && !targetIds.Contains(id))
                {
                    targetIds.Add(id);
                }
            }
        }
        // Validate every target id exists (the set always includes the primary
        // targetId) BEFORE inserting the deployment, so a bogus or cross-Space id
        // fails fast here with a clear message instead of opaquely at dispatch.
        var existing = await db.DeploymentTargets
            .Where(t => targetIds.Contains(t.Id))
            .Select(t => t.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        var missing = targetIds.Where(id => !existing.Contains(id)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Target(s) not found: {string.Join(", ", missing)}.");
        }

        // Enforce lifecycle phase gate (throws if gate not satisfied).
        await EnforceLifecycleGateAsync(db, releaseId, environmentId, tenantId, ct).ConfigureAwait(false);

        // B1/T1-2: exactly ONE dispatch path per deployment. A due/past
        // scheduledFor is normalized to null and dispatched immediately below —
        // previously the past value was persisted AND the row enqueued, so the
        // minutely dispatch job re-enqueued it during the worker's prep window
        // (double-dispatch). Only a genuinely FUTURE instant is persisted, and
        // then the scheduled job is the sole dispatcher.
        // Normalize to UTC first: Npgsql rejects a DateTimeOffset with a non-zero
        // offset on a timestamptz column, and the deploy dialog builds a
        // local-offset instant, so persisting verbatim throws at SaveChanges on a
        // non-UTC host.
        var scheduledUtc = scheduledFor?.ToUniversalTime();
        var isScheduledForFuture = scheduledUtc.HasValue &&
            scheduledUtc.Value > time.GetUtcNow();

        var deployment = new Deployment
        {
            ReleaseId = releaseId,
            ProjectId = release.ProjectId,
            ChannelId = release.ChannelId,
            EnvironmentId = environmentId,
            TenantId = tenantId,
            Status = DeploymentStatus.Queued,
            FailureMode = failureMode,
            ScheduledFor = isScheduledForFuture ? scheduledUtc : null,
            ParentTaskId = parentTaskId,
            FormValues = promptedValues is { Count: > 0 }
                ? System.Text.Json.JsonSerializer.Serialize(promptedValues)
                : null,
        };
        initiator.StampOnto(deployment);   // provenance (fix 6)

        // Persist the target set in the SAME change set as the deployment so
        // both commit atomically (parity with RunbookService.TriggerAsync) — a
        // crash between two saves would else leave a Queued deployment with no
        // assignments that the stale-Queued reconciler re-signals into an
        // empty-target-set dispatch (spurious "No target assigned" failure).
        // deployment.Id is a client-generated key, available before the save.
        // AddedUtc gets a strictly increasing MICROSECOND per row so assignment
        // ORDER survives the DB round-trip (Postgres timestamptz stores
        // microseconds — sub-µs ticks would collapse to equal values). Readers
        // (DeploymentTargetSetExtensions.ResolvedTargets) sort by it and treat
        // the first-assigned target as canonical.
        var now = time.GetUtcNow();
        for (var i = 0; i < targetIds.Count; i++)
        {
            db.TaskTargetAssignments.Add(new TaskTargetAssignment
            {
                TaskId   = deployment.Id,
                TargetId = targetIds[i],
                AddedUtc = now.AddMicroseconds(i),
            });
        }
        db.Deployments.Add(deployment);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Dispatch immediately unless the caller requested a future start time.
        if (!isScheduledForFuture)
        {
            var accountId = accountContext.IsResolved ? accountContext.CurrentAccountId : Guid.Empty;
            await deploymentQueue.Writer
                .WriteAsync(new TenantWorkItem(accountId, deployment.Id), ct)
                .ConfigureAwait(false);
        }

        return deployment;
    }

    // ── Cancel ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Transitions a non-terminal deployment to
    /// <see cref="DeploymentStatus.Cancelled"/> and stamps its completion time.
    /// <para>
    /// Effectiveness depends on where the deployment is in its lifecycle:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Queued / scheduled</b>: the <see cref="DeploymentWorker"/>'s
    ///     dequeue-skip check bails on a <c>Cancelled</c> row, so no work is ever
    ///     dispatched — this is the "cancelling a pending deployment prevents
    ///     dispatch" guarantee. <c>ScheduledFor</c> is also cleared so the
    ///     Hangfire re-dispatch job can never pick it up.</item>
    ///   <item><b>Running</b>: the recorded verdict is pushed to the connected
    ///     agent(s) as a cooperative abort (B6) — the running step's process
    ///     tree is killed within seconds and the attempt reports a swallowed
    ///     late failure. When the agent is offline (or the push is lost) the
    ///     pre-B6 fallback applies: the wave runs to completion and the
    ///     orchestrator stops at the next wave boundary. Either way this
    ///     terminal status stands (B5 guards).</item>
    /// </list>
    /// <para>
    /// Returns the updated deployment, or <c>null</c> when it does not exist.
    /// Throws <see cref="InvalidOperationException"/> when the deployment is
    /// already in a terminal state.
    /// </para>
    /// </summary>
    public async Task<Deployment?> CancelAsync(
        Guid id, CallerAuthorization caller, CancellationToken ct = default)
    {
        // D1 Phase 2 — shared cancel core (T1-8 scope probe → B5 guarded flip →
        // B6 abort push). Saving a modified AuditableEntity auto-emits a
        // "Deployment.Updated" audit row via AuditLogInterceptor.
        var deployment = await ServerTaskCanceller.CancelAsync<Deployment>(
            dbFactory, permissions, time, cancelPusher, id, caller,
            taskNoun: "Deployment",
            pushReason: "Cancelled by operator.",
            ct).ConfigureAwait(false);

        // Record the SEMANTIC cancel event here, not at each call site, so no
        // cancel surface (UI, REST, a future CLI/MCP/bulk/auto-cancel) can omit
        // it. A non-null return means the guarded flip won (already-terminal
        // throws; not-found returns null), so this fires once per real cancel.
        if (deployment is not null && auditLog is not null)
        {
            await auditLog.RecordAsync(
                AuditEventType.DeploymentCancelled,
                subjectType: "Deployment",
                subjectId:   id.ToString(),
                details:     "Deployment cancelled.",
                ct:          ct).ConfigureAwait(false);
        }
        return deployment;
    }

    // ── Query ──────────────────────────────────────────────────────────────

    public async Task<List<Deployment>> GetAllAsync(
        Guid? projectId = null, int? limit = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var q = db.Deployments
            .Include(d => d.Release).ThenInclude(r => r.Project)
            .Include(d => d.Environment)
            .Include(d => d.Targets).ThenInclude(a => a.Target)
            .Include(d => d.Tenant)
            .AsQueryable();

        if (projectId.HasValue)
        {
            q = q.Where(d => d.ProjectId == projectId.Value);
        }

        var ordered = q.OrderByDescending(d => d.CreatedUtc);
        // Cap the row count when a limit is given (e.g. the global Tasks page)
        // so an instance with a long history doesn't materialize every row.
        var bounded = limit is > 0 ? ordered.Take(limit.Value) : (IQueryable<Deployment>)ordered;
        return await bounded.ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Deployments that ran on one target (newest first, bounded) —
    /// powers the target-detail Deployments tab. Matches via the
    /// assignments join, the single authority for the target set.</summary>
    public async Task<List<Deployment>> GetForTargetAsync(
        Guid targetId, int limit = 100, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Deployments
            .Include(d => d.Release).ThenInclude(r => r.Project)
            .Include(d => d.Environment)
            // The full assignment set, not just the filter match — the Tasks
            // page's ?target= rows render the target column from it (F6).
            .Include(d => d.Targets).ThenInclude(a => a.Target)
            .Include(d => d.Tenant)
            .Where(d => d.Targets.Any(a => a.TargetId == targetId))
            .OrderByDescending(d => d.CreatedUtc)
            .Take(limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>Every deployment of one release (newest first) — powers the
    /// release-detail page's deployment history.</summary>
    public async Task<List<Deployment>> GetForReleaseAsync(
        Guid releaseId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Deployments
            .Include(d => d.Environment)
            .Include(d => d.Targets).ThenInclude(a => a.Target)
            .Include(d => d.Tenant)
            .Where(d => d.ReleaseId == releaseId)
            .OrderByDescending(d => d.CreatedUtc)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<Deployment?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Include the multi-target join so the deployment-detail page can
        // render the target set + map per-outcome TargetIds to
        // human-readable names without a second round-trip.
        return await db.Deployments
            .Include(d => d.Release).ThenInclude(r => r.Project)
            .Include(d => d.Environment)
            .Include(d => d.Targets).ThenInclude(a => a.Target!)
            .FirstOrDefaultAsync(d => d.Id == id, ct);
    }

    /// <summary>
    /// The deployment's log in sequence order, stitched from compacted step blobs +
    /// live staging. Resolves the deployment first (Space-filtered) so the
    /// not-ISpaceScoped log tables are only reached via an authorized id. Pass
    /// <paramref name="afterSequence"/> to tail incrementally (only lines with a
    /// higher sequence); the default (-1) returns the full log.
    /// </summary>
    public async Task<List<TaskLogLine>> GetLogAsync(
        Guid deploymentId, int afterSequence = -1, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var exists = await db.Deployments.AnyAsync(d => d.Id == deploymentId, ct).ConfigureAwait(false);
        if (!exists)
        {
            return [];
        }
        return await TaskLogService.ReadSinceAsync(db, deploymentId, afterSequence, ct).ConfigureAwait(false);
    }

    /// <summary>Whether a deployment with this id exists in the active Space.
    /// The kind gate for the artifact read endpoints — a runbook-run id (or a
    /// deployment in another Space) returns <c>false</c>, so those endpoints 404
    /// rather than serving another kind's children under a /deployments/ route
    /// (parity with <c>RunbookService.RunExistsAsync</c>).</summary>
    public async Task<bool> ExistsAsync(Guid deploymentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Deployments.AnyAsync(d => d.Id == deploymentId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// F1 read-side helper — is a <c>Queued</c> deployment blocked by the
    /// (project, environment, tenant) serialization rule, i.e. is another
    /// deployment of the same key currently in-flight (claimed but not terminal —
    /// <c>Running</c> or a parked offline-drop <c>PendingOfflineResult</c>)?
    /// Powers the DeploymentDetail queue-reason banner. Uses the SAME predicate
    /// the claim enforces (<see cref="ServerTaskLease.InFlightDeploymentPeerPredicate"/>)
    /// so the shown reason can never drift from the actual gate. Space-scoped by
    /// the global query filter (same-key deployments share one Space); no new state.
    /// </summary>
    public async Task<bool> HasInFlightPeerAsync(
        Guid queuedDeploymentId, Guid projectId, Guid environmentId, Guid? tenantId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.ServerTasks
            .AnyAsync(
                ServerTaskLease.InFlightDeploymentPeerPredicate(
                    queuedDeploymentId, projectId, environmentId, tenantId),
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// F1 read-side helper #2 — the FULL claim-order deferral
    /// (<see cref="ServerTaskLease.ClaimDeferralPredicate"/>: an in-flight peer OR
    /// an older already-due <c>Queued</c> sibling). The detail page consults this
    /// BEFORE the F6 target probe so an F1-FIFO refusal is never misattributed to
    /// the target rule — the claim checks F1 first, and a same-key sibling
    /// usually shares targets, so the narrower <see cref="HasInFlightPeerAsync"/>
    /// alone would let the F6 arm capture (and mislabel) the wait.
    /// </summary>
    public async Task<bool> HasClaimDeferringPeerAsync(
        Guid queuedDeploymentId, Guid projectId, Guid environmentId, Guid? tenantId,
        DateTimeOffset createdUtc, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.ServerTasks
            .AnyAsync(
                ServerTaskLease.ClaimDeferralPredicate(
                    queuedDeploymentId, projectId, environmentId, tenantId,
                    createdUtc, time.GetUtcNow()),
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// F6 read-side helper — the target-wait reason for a still-<c>Queued</c>
    /// task, or <c>null</c> when no serial target is contended. The UI read of
    /// the SAME query the claim refuses on (<see cref="ServerTaskTargetExclusion"/>),
    /// so the banner can never drift from the actual gate — the
    /// <see cref="HasInFlightPeerAsync"/> discipline extended to the target
    /// dimension. Kind-agnostic (the underlying read is by task id): the ONE
    /// wrapper both detail pages call, deliberately, so the two banners cannot
    /// drift.
    /// </summary>
    public async Task<ServerTaskTargetExclusion.TargetConflict?> GetTargetConflictAsync(
        Guid taskId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await ServerTaskTargetExclusion
            .DescribeConflictAsync(db, taskId, time.GetUtcNow(), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// M11.C — the AI diagnosis for a deployment, or null when none has been
    /// produced (AI disabled, diagnosis still running, or the deployment
    /// succeeded). Powers the "AI Analysis" card on the detail page.
    /// </summary>
    public async Task<KrakenDeploy.Server.Core.Domain.Ai.DeploymentDiagnosis?> GetDiagnosisAsync(
        Guid deploymentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.DeploymentDiagnoses
            .FirstOrDefaultAsync(x => x.DeploymentId == deploymentId, ct);
    }

    /// <summary>
    /// Returns all output variables captured during a deployment via
    /// <c>Set-OctopusVariable</c> / <c>##octopus[setVariable]</c> markers,
    /// ordered by step capture order and then variable name.
    /// <para>
    /// T0-6: sensitive rows store ciphertext; their <see cref="TaskOutputVariable.Value"/>
    /// is masked to <c>***</c> here so no caller (UI or API) ever sees the
    /// ciphertext or the secret. The value is never decrypted for display.
    /// </para>
    /// </summary>
    public async Task<List<TaskOutputVariable>> GetOutputVariablesAsync(
        Guid deploymentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.TaskOutputVariables
            .Where(o => o.TaskId == deploymentId)
            .OrderBy(o => o.CapturedUtc)
            .ThenBy(o => o.Name)
            .ToListAsync(ct);

        // Mask at the boundary (rows are detached — mutation is not persisted).
        foreach (var row in rows)
        {
            if (row.IsSensitive)
            {
                row.Value = "***";
            }
        }

        return rows;
    }

    /// <summary>
    /// M14.5 — returns the terminal per-step outcomes captured during a
    /// deployment, ordered by <see cref="DeploymentStepOutcome.StepIndex"/>
    /// (== SortOrder rank in the process). Powers the deployment detail
    /// page's Steps tab.
    /// </summary>
    public async Task<List<TaskStepOutcome>> GetStepOutcomesAsync(
        Guid deploymentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TaskStepOutcomes
            .Where(o => o.TaskId == deploymentId)
            .OrderBy(o => o.StepIndex)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Builds a Tenant × Environment matrix of the latest deployment per cell
    /// for the given project. Returns every connected tenant and every space
    /// environment regardless of whether any deployment exists yet — empty
    /// cells are signalled by missing dictionary keys, not null values.
    /// </summary>
    public async Task<ProjectDashboardMatrix> GetProjectMatrixAsync(
        Guid projectId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Tenants connected to this project (many-to-many via the Project.Tenants
        // navigation), ordered alphabetically for stable display.
        var tenants = await db.Projects
            .Where(p => p.Id == projectId)
            .SelectMany(p => p.Tenants)
            .OrderBy(t => t.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // All environments in the current Space (the global query filter scopes
        // this automatically through ISpaceScoped).
        var environments = await db.Environments
            .OrderBy(e => e.SortOrder).ThenBy(e => e.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Latest deployment per (tenantId, environmentId) for this project.
        // GroupBy + First-by-CreatedUtc would force client evaluation, so we
        // pull every deployment for the project (typically a small set) and
        // fold in memory.
        var rows = await db.Deployments
            .Where(d => d.ProjectId == projectId && d.TenantId != null)
            .Include(d => d.Release).ThenInclude(r => r.Channel)
            .OrderByDescending(d => d.CreatedUtc)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var cells = new Dictionary<(Guid, Guid), DashboardCell>();
        foreach (var d in rows)
        {
            if (d.TenantId is null)
            {
                continue;
            }

            var key = (d.TenantId.Value, d.EnvironmentId);
            // First wins because the rows are ordered desc — that's the latest.
            if (cells.ContainsKey(key))
            {
                continue;
            }

            cells[key] = new DashboardCell(
                d.Id,
                d.Status,
                d.Release.Version,
                d.Release.Channel?.Name,
                d.CreatedUtc);
        }

        return new ProjectDashboardMatrix(tenants, environments, cells);
    }

    // ── Lifecycle gate ──────────────────────────────────────────────────────

    /// <summary>
    /// Query-shaped twin of the create-time gate: for each environment in
    /// <paramref name="environmentIds"/>, would the lifecycle allow deploying
    /// this release there right now (per-tenant progression when
    /// <paramref name="tenantId"/> is set)? Feeds the deploy dialog's
    /// environment picker so illegal choices surface BEFORE submit. Shares the
    /// exact evaluation <see cref="CreateAsync"/> enforces — the dialog can
    /// display the same message the create would throw.
    /// </summary>
    public async Task<Dictionary<Guid, LifecycleGateStatus>> GetLifecycleGateStatusesAsync(
        Guid releaseId,
        Guid? tenantId,
        IReadOnlyCollection<Guid> environmentIds,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await ComputeLifecycleGateStatusesAsync(db, releaseId, tenantId, environmentIds, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Checks whether all earlier non-optional lifecycle phases have been satisfied
    /// for this release before allowing deployment to <paramref name="environmentId"/>.
    /// Silently succeeds if no lifecycle is configured.
    /// </summary>
    private static async Task EnforceLifecycleGateAsync(
        KrakenDbContext db, Guid releaseId, Guid environmentId, Guid? tenantId, CancellationToken ct)
    {
        var statuses = await ComputeLifecycleGateStatusesAsync(
            db, releaseId, tenantId, [environmentId], ct).ConfigureAwait(false);
        if (statuses.TryGetValue(environmentId, out var status) && !status.Allowed)
        {
            throw new InvalidOperationException(status.Reason);
        }
    }

    private static async Task<Dictionary<Guid, LifecycleGateStatus>> ComputeLifecycleGateStatusesAsync(
        KrakenDbContext db,
        Guid releaseId,
        Guid? tenantId,
        IReadOnlyCollection<Guid> environmentIds,
        CancellationToken ct)
    {
        // Default allow: no lifecycle, unknown release, or env outside the
        // lifecycle's phases all mean "no gate".
        var result = environmentIds.Distinct()
            .ToDictionary(id => id, _ => LifecycleGateStatus.Ok);

        // Load the lifecycle via: release → channel → lifecycle,
        // OR release → project → lifecycle (fallback).
        var release = await db.Releases
            .Include(r => r.Channel)
                .ThenInclude(c => c!.Lifecycle)
            .Include(r => r.Project)
                .ThenInclude(p => p.Lifecycle)
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == releaseId, ct)
            .ConfigureAwait(false);

        if (release is null)
        {
            return result;
        }

        var lifecycle = release.Channel?.Lifecycle ?? release.Project.Lifecycle;
        if (lifecycle is null || lifecycle.Phases.Count == 0)
        {
            return result;
        }

        var phases = lifecycle.Phases.OrderBy(p => p.SortOrder).ToList();

        // One success-count per required phase (NOT per environment) — the
        // per-environment walk below is then pure lookup.
        var successCounts = new Dictionary<int, int>();
        for (var i = 0; i < phases.Count; i++)
        {
            var phase = phases[i];
            if (phase.IsOptional || phase.EnvironmentIds.Count == 0)
            {
                continue;
            }

            // Count distinct environments in this phase that have a successful deployment.
            var envIds = phase.EnvironmentIds;
            var successQuery = db.Deployments
                .Where(d => d.ReleaseId == releaseId &&
                            envIds.Contains(d.EnvironmentId) &&
                            d.Status == DeploymentStatus.Succeeded);

            if (tenantId.HasValue)
            {
                successQuery = successQuery.Where(d => d.TenantId == tenantId.Value);
            }

            successCounts[i] = await successQuery
                .Select(d => d.EnvironmentId)
                .Distinct()
                .CountAsync(ct)
                .ConfigureAwait(false);
        }

        foreach (var environmentId in result.Keys.ToList())
        {
            // Find the index of the target environment's phase.
            var targetIdx = phases.FindIndex(p =>
                p.EnvironmentIds.Contains(environmentId) ||
                p.OptionalEnvironmentIds.Contains(environmentId));

            if (targetIdx <= 0)
            {
                continue; // first phase, or environment not covered by lifecycle — allow
            }

            // Check all required phases before the target phase.
            for (var i = 0; i < targetIdx; i++)
            {
                var phase = phases[i];
                if (phase.IsOptional || phase.EnvironmentIds.Count == 0)
                {
                    continue;
                }

                var minRequired = phase.MinimumEnvironments == 0
                    ? phase.EnvironmentIds.Count
                    : phase.MinimumEnvironments;

                var successCount = successCounts[i];
                if (successCount < minRequired)
                {
                    result[environmentId] = new LifecycleGateStatus(false,
                        $"Lifecycle gate: phase '{phase.Name}' requires successful deployment to " +
                        $"{minRequired} environment(s) but only {successCount} have succeeded for this release. " +
                        "Deploy to the required earlier environments first.");
                    break;
                }
            }
        }

        return result;
    }
}

/// <summary>Lifecycle-gate verdict for one environment: deployable now, or
/// blocked with the operator-facing reason (the same message
/// <see cref="DeploymentService.CreateAsync"/> would throw).</summary>
public sealed record LifecycleGateStatus(bool Allowed, string? Reason)
{
    public static readonly LifecycleGateStatus Ok = new(true, null);
}
