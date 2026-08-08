using System.Globalization;
using System.Security.Claims;
using System.Threading.Channels;
using KrakenDeploy.Server.Core.Domain.Accounts;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Spaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// WP3 — the authority for reading and answering manual-intervention gates.
/// <para>
/// Every caller goes through here: the Blazor dialog, the REST endpoint and the
/// timeout sweeper. The <c>&lt;RequirePermission&gt;</c> UI gate and
/// <c>UiActionGuard</c> are pre-checks; THIS class decides, because a Blazor handler
/// runs over the SignalR circuit outside the HTTP authorization middleware
/// (house rule 2).
/// </para>
/// <para>
/// Two independent authorization checks apply to a response, and both must pass:
/// the scoped <see cref="Permission.InterruptionViewSubmitResponsible"/> grant, AND —
/// when the step named responsible teams — membership of one of them. The second is
/// not a permission and cannot be expressed as one: it is per-STEP data, chosen by
/// the process author, which is exactly the Octopus model.
/// </para>
/// </summary>
public sealed class InterruptionService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    IPermissionEvaluator permissions,
    IAuditLog auditLog,
    ISpaceContext spaceContext,
    Channel<TenantWorkItem> taskQueue,
    IAccountContext accountContext,
    TimeProvider time,
    ILogger<InterruptionService> logger)
{
    /// <summary>Max length accepted for responder notes — matches the column.</summary>
    public const int MaxNotesLength = 4000;

    /// <summary>
    /// The gates on one task, newest first, for the detail page's banner + history.
    /// Requires <see cref="Permission.InterruptionView"/> scoped to the task.
    /// </summary>
    public async Task<IReadOnlyList<Interruption>> ListForTaskAsync(
        Guid taskId, CallerAuthorization caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        await EnsureScopedForTaskAsync(db, taskId, caller, Permission.InterruptionView, ct)
            .ConfigureAwait(false);

        return await db.Interruptions
            .IgnoreQueryFilters()
            .Where(i => i.TaskId == taskId)
            .OrderByDescending(i => i.CreatedUtc)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The task ids (of those given) that currently have a PENDING gate — powers the
    /// "waiting for approval" indicator on the Deployments and Tasks grids without an
    /// N+1 per row. Requires <see cref="Permission.InterruptionView"/> in the active
    /// Space; the ids are already Space-filtered by the caller's own query, so this is
    /// a Space-level check rather than a per-task one.
    /// </summary>
    public async Task<HashSet<Guid>> FindTasksAwaitingResponseAsync(
        IReadOnlyCollection<Guid> taskIds,
        Guid spaceId,
        CallerAuthorization caller,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(taskIds);
        ArgumentNullException.ThrowIfNull(caller);
        if (taskIds.Count == 0)
        {
            return [];
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (!caller.IsSystem
            && !await permissions.HasPermissionAsync(
                    caller.User!, Permission.InterruptionView,
                    new PermissionScope(SpaceId: spaceId), ct: ct).ConfigureAwait(false))
        {
            // Not an exception: the indicator is decoration, and a user without the
            // read permission simply sees none.
            return [];
        }

        // Constrain the Space in the QUERY, not just via the caller's permission on a
        // caller-supplied spaceId. The read runs IgnoreQueryFilters, so without this
        // predicate the only thing standing between a foreign task id and a
        // "task X is awaiting approval" disclosure is the convention that callers pass
        // ids from an already-Space-filtered read.
        //
        // The task-status term is now actually PRESENT (WP3-b): this comment used to
        // claim it excluded gates whose task had gone terminal, and it did not — the
        // guarantee rested entirely on both callers happening to pre-filter to Paused.
        // Paused rather than "not terminal" because that is the only state in which a
        // pending gate is answerable at all; a Pending gate on a Running task is an
        // invariant violation the orchestrator refuses, not something to advertise.
        var ids = taskIds as ICollection<Guid> ?? [.. taskIds];
        return [.. await db.Interruptions
            .IgnoreQueryFilters()
            .Where(i => i.Status == InterruptionStatus.Pending
                     && i.SpaceId == spaceId
                     && i.Task.Status == DeploymentStatus.Paused
                     && ids.Contains(i.TaskId))
            .Select(i => i.TaskId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false)];
    }

    /// <summary>
    /// Whether <paramref name="caller"/> may answer this specific gate: the scoped
    /// respond permission AND (when the gate names teams) membership of one of them.
    /// Used by the UI to decide whether to offer the buttons at all; the mutating
    /// calls re-check it themselves, so a stale UI cannot bypass it.
    /// </summary>
    public async Task<bool> CanRespondAsync(
        Guid interruptionId, CallerAuthorization caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var interruption = await LoadAsync(db, interruptionId, ct).ConfigureAwait(false);
        if (interruption is null)
        {
            return false;
        }

        // The TASK must still be answerable — the same guard RespondAsync enforces
        // (WP3-b). Without it the panel offered Approve/Reject forever on a gate whose
        // task had gone terminal by any route other than ServerTaskCanceller (which
        // closes gates): FailAsync accepts Paused, so a paused task can reach Failed with
        // its gate still Pending, InterruptionTimeoutJob now deliberately skips such a
        // row, and nothing else ever closes it. Every click then got a 409 from the
        // mutating path — a permanently live button that cannot work.
        if (interruption.Task.Status.IsTerminal())
        {
            return false;
        }

        if (caller.IsSystem)
        {
            return true;
        }

        return await HasScopedRespondPermissionAsync(interruption, caller, ct)
                   .ConfigureAwait(false)
               && (await IsResponsibleAsync(interruption, caller, ct)
                   .ConfigureAwait(false)).Allowed;
    }

    /// <summary>
    /// Approves a gate and wakes the orchestrator to resume the task.
    /// <paramref name="notes"/> is optional on approve.
    /// </summary>
    public Task<Interruption> ApproveAsync(
        Guid interruptionId, string? notes, CallerAuthorization caller,
        CancellationToken ct = default)
        => RespondAsync(interruptionId, InterruptionStatus.Approved, notes, caller, ct);

    /// <summary>
    /// Rejects a gate and wakes the orchestrator, which runs the task's
    /// <c>Failure</c>/<c>Always</c> cleanup steps and then finalises it
    /// <c>Failed</c>.
    /// <para>
    /// <paramref name="notes"/> is MANDATORY here, enforced in this method rather than
    /// only in the dialog: "why was this change refused" is the single most useful
    /// line in a change-control review, and a REST or CLI caller must not be able to
    /// skip it.
    /// </para>
    /// </summary>
    public Task<Interruption> RejectAsync(
        Guid interruptionId, string notes, CallerAuthorization caller,
        CancellationToken ct = default)
        => RespondAsync(interruptionId, InterruptionStatus.Rejected, notes, caller, ct);

    private async Task<Interruption> RespondAsync(
        Guid interruptionId,
        InterruptionStatus resolution,
        string? notes,
        CallerAuthorization caller,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (resolution == InterruptionStatus.Rejected && string.IsNullOrWhiteSpace(notes))
        {
            throw new ArgumentException(
                "Notes are required when rejecting a manual intervention — record why the " +
                "change was refused.", nameof(notes));
        }
        if (notes is { Length: > MaxNotesLength })
        {
            throw new ArgumentException(
                $"Notes exceed the {MaxNotesLength.ToString(CultureInfo.InvariantCulture)}-character " +
                "limit.", nameof(notes));
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var interruption = await LoadAsync(db, interruptionId, ct).ConfigureAwait(false)
            // Typed so the REST layer can answer 404 AFTER its authz arms -- see
            // InterruptionNotFoundException. A caller who cannot reach the gate's Space
            // must not be able to distinguish "missing" from "exists elsewhere".
            ?? throw new InterruptionNotFoundException(interruptionId);

        var responsible = new ResponsibleVerdict(Allowed: true, BreakGlass: false);
        if (!caller.IsSystem)
        {
            // Scoped permission first — EnsureScopedAsync throws the 403-mapped
            // exception the rest of the surface uses.
            await permissions.EnsureScopedAsync(
                caller, Permission.InterruptionViewSubmitResponsible,
                ScopeOf(interruption.Task), ct).ConfigureAwait(false);

            responsible = await IsResponsibleAsync(interruption, caller, ct)
                .ConfigureAwait(false);
            if (!responsible.Allowed)
            {
                throw new UnauthorizedAccessException(
                    $"Manual intervention '{interruption.StepName}' may only be answered by " +
                    "a member of its responsible team(s).");
            }
        }

        // The TASK must still be answerable. Guarding only the gate's own status let a
        // reviewer "approve" a deployment that had already been cancelled or failed —
        // writing an InterventionApproved audit row, and firing an M13.B notification,
        // naming a real person for a change that never ran. That is the precise
        // inversion of what the change-control trail exists to prove, so it is refused
        // here even though nothing would actually deploy.
        if (interruption.Task.Status.IsTerminal())
        {
            throw new InvalidOperationException(
                $"Manual intervention '{interruption.StepName}' can no longer be answered: " +
                $"the {InterruptionAuditEvents.SubjectType(interruption.Task.Kind).ToLowerInvariant()} " +
                $"is already {interruption.Task.Status}. Recording a decision now would put an " +
                "approval in the audit trail for a change that never ran.");
        }

        // Guard the transition on Pending so a double-click, a duplicate REST call, or
        // a human racing the timeout sweeper cannot overwrite a recorded decision. The
        // conditional UPDATE is the authority, not the read above.
        var now = time.GetUtcNow();
        var (actedByUserId, display) = ResponderLabel(caller);
        var rows = await db.Interruptions
            .IgnoreQueryFilters()
            .Where(i => i.Id == interruptionId && i.Status == InterruptionStatus.Pending)
            .ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.Status, resolution)
                    .SetProperty(i => i.ActedByUserId, actedByUserId)
                    .SetProperty(i => i.ActedByDisplay, display)
                    .SetProperty(i => i.Notes, notes)
                    .SetProperty(i => i.ActedUtc, now),
                ct)
            .ConfigureAwait(false);
        if (rows != 1)
        {
            // Re-read so the message names the decision that actually stands.
            var current = await db.Interruptions.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(i => i.Id == interruptionId, ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Manual intervention '{interruption.StepName}' has already been " +
                $"{current?.Status.ToString().ToLowerInvariant() ?? "resolved"}" +
                (current?.ActedByDisplay is { } who ? $" by {who}" : "") + ".");
        }

        interruption.Status         = resolution;
        interruption.ActedByUserId  = actedByUserId;
        interruption.ActedByDisplay = display;
        interruption.Notes          = notes;
        interruption.ActedUtc       = now;

        await RecordResolutionAsync(interruption, responsible.BreakGlass, ct)
            .ConfigureAwait(false);
        await SignalResumeAsync(interruption, ct).ConfigureAwait(false);
        return interruption;
    }

    /// <summary>
    /// Marks a gate <see cref="InterruptionStatus.TimedOut"/> on behalf of the sweeper
    /// and wakes the orchestrator. Separate from <see cref="RespondAsync"/> because
    /// there is no caller to authorize and no notes to demand — the sweeper IS the
    /// system. Returns <c>false</c> when a human answered first (the conditional
    /// UPDATE lost), in which case their decision stands untouched.
    /// </summary>
    internal async Task<bool> ExpireAsync(Guid interruptionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var interruption = await LoadAsync(db, interruptionId, ct).ConfigureAwait(false);
        if (interruption is null)
        {
            return false;
        }

        var now = time.GetUtcNow();
        var rows = await db.Interruptions
            .IgnoreQueryFilters()
            .Where(i => i.Id == interruptionId && i.Status == InterruptionStatus.Pending)
            .ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.Status, InterruptionStatus.TimedOut)
                    // No ActedByUserId — nobody acted. The display label is what a
                    // reviewer reads, so it says so explicitly rather than blank.
                    .SetProperty(i => i.ActedByDisplay, "System (approval timeout)")
                    .SetProperty(i => i.ActedUtc, now),
                ct)
            .ConfigureAwait(false);
        if (rows != 1)
        {
            return false;
        }

        interruption.Status         = InterruptionStatus.TimedOut;
        interruption.ActedByDisplay = "System (approval timeout)";
        interruption.ActedUtc       = now;

        logger.LogWarning(
            "Manual intervention {InterruptionId} ('{Step}') on task {TaskId} expired " +
            "unanswered at {Expiry}; the task will fail with its cleanup steps.",
            interruption.Id, interruption.StepName, interruption.TaskId,
            interruption.ExpiresUtc);

        await RecordResolutionAsync(interruption, breakGlass: false, ct)
            .ConfigureAwait(false);
        await SignalResumeAsync(interruption, ct).ConfigureAwait(false);
        return true;
    }

    // ── Internals ───────────────────────────────────────────────────────────

    /// <summary>
    /// Emits the resolution audit event at DECISION time — not when the orchestrator
    /// gets round to resuming. That ordering matters twice over: the row timestamps
    /// when the human actually acted, and an M13.B subscription notifies immediately
    /// even if the resume is delayed by maintenance mode or a restart.
    /// <para>
    /// This entry is the DURABLE change-control record, so it must be SELF-CONTAINED
    /// (WP3-b). The <c>interruptions</c> row is CASCADE-deleted with its task and
    /// <c>RetentionService</c> hard-deletes tasks, so "who approved this release, when,
    /// against which responsible teams, with what notes" has to be answerable from this
    /// string alone — including the responsible-team NAMES, which are unrecoverable once
    /// a team is deleted. The <c>Interruption.*</c> event types are exempt from the
    /// ordinary audit window via the <c>ChangeControlAuditDays</c> retention class.
    /// </para>
    private async Task RecordResolutionAsync(
        Interruption interruption, bool breakGlass, CancellationToken ct)
    {
        // Stamp the TASK's Space, not the ambient one. AuditLogService uses
        // spaceCtx.CurrentSpaceId, and two of the three callers have no Space: a
        // Hangfire sweeper scope and the /api surface both fall back to
        // WellKnown.DefaultSpaceId. So a Prod-Space gate filed its decision under
        // Default, where ApplySpaceVisibility hid it from the auditors who own the
        // deployment — and SubscriptionMatcher (which compares evt.SpaceId to the
        // subscription's Space) fired a DEFAULT-Space subscription instead, pushing
        // another Space's task id, step name, responder and notes across the isolation
        // boundary. Same pattern DeploymentWorker and RetentionService already use.
        using var scope = spaceContext.WithSpace(interruption.SpaceId);
        await auditLog.RecordAsync(
            InterruptionAuditEvents.For(interruption.Task.Kind, interruption.Status),
            subjectType: InterruptionAuditEvents.SubjectType(interruption.Task.Kind),
            subjectId:   interruption.TaskId.ToString(),
            details:     $"InterruptionId={interruption.Id}, Step={interruption.StepName}, " +
                         $"StepIndex={interruption.StepIndex.ToString(CultureInfo.InvariantCulture)}, " +
                         $"Decision={interruption.Status}, " +
                         $"ResponsibleTeams={DescribeResponsible(interruption)}, " +
                         $"PausedUtc={interruption.CreatedUtc:O}, " +
                         $"DecidedUtc={interruption.ActedUtc?.ToString("O") ?? "<none>"}, " +
                         $"RespondedBy={interruption.ActedByDisplay ?? "<none>"}" +
                         (interruption.ActedByUserId is { } uid ? $", RespondedByUserId={uid}" : "") +
                         (breakGlass
                             ? ", Override=AdministerSystem (responder is not a member of " +
                               "the responsible team(s))"
                             : "") +
                         (string.IsNullOrWhiteSpace(interruption.Notes)
                             ? "" : $", Notes={interruption.Notes}"),
            ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The approver set as a reviewer needs to read it: names where we have them, ids as
    /// a fallback, and the explicit "anyone with the permission" wording for the empty
    /// case — which must never be rendered as an empty list, because "no restriction"
    /// and "restricted to nobody" are opposite facts.
    /// </summary>
    private static string DescribeResponsible(Interruption interruption)
    {
        if (interruption.ResponsibleTeamIds.Length == 0)
        {
            return "<any responder with the permission>";
        }
        return interruption.ResponsibleTeamNames.Length > 0
            ? string.Join("; ", interruption.ResponsibleTeamNames)
            : string.Join("; ", interruption.ResponsibleTeamIds);
    }

    /// <summary>
    /// Wakes the orchestrator. At-least-once by design (B1): the DB — the resolved
    /// interruption plus the task's <c>Paused</c> status — is the source of truth, and
    /// the reconciler's pause arm re-signals anything this write loses to a restart.
    /// The conditional <c>Paused → Running</c> resume makes duplicates harmless.
    /// </summary>
    private async Task SignalResumeAsync(Interruption interruption, CancellationToken ct)
    {
        var accountId = accountContext.IsResolved ? accountContext.CurrentAccountId : Guid.Empty;
        await taskQueue.Writer
            .WriteAsync(new TenantWorkItem(accountId, interruption.TaskId), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Loads a gate with its parent task (needed for the permission scope and
    /// the kind-branched audit vocabulary), filter-free so a gate in a non-active Space
    /// resolves and then fails the SCOPE check rather than silently 404-ing.</summary>
    private static Task<Interruption?> LoadAsync(
        KrakenDbContext db, Guid interruptionId, CancellationToken ct)
        => db.Interruptions
            .IgnoreQueryFilters()
            .Include(i => i.Task)
            .FirstOrDefaultAsync(i => i.Id == interruptionId, ct);

    private static PermissionScope ScopeOf(ServerTask task)
        => new(SpaceId: task.SpaceId, ProjectId: task.ProjectId,
               EnvironmentId: task.EnvironmentId, TenantId: task.TenantId);

    private async Task EnsureScopedForTaskAsync(
        KrakenDbContext db, Guid taskId, CallerAuthorization caller,
        Permission permission, CancellationToken ct)
    {
        if (caller.IsSystem)
        {
            return;
        }
        var s = await db.ServerTasks.IgnoreQueryFilters()
            .Where(t => t.Id == taskId)
            .Select(t => new { t.SpaceId, t.ProjectId, t.EnvironmentId, t.TenantId })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        await permissions.EnsureScopedAsync(
            caller, permission,
            new PermissionScope(
                SpaceId: s?.SpaceId, ProjectId: s?.ProjectId,
                EnvironmentId: s?.EnvironmentId, TenantId: s?.TenantId), ct)
            .ConfigureAwait(false);
    }

    private async Task<bool> HasScopedRespondPermissionAsync(
        Interruption interruption, CallerAuthorization caller, CancellationToken ct)
        => caller.User is not null
           && await permissions.HasPermissionAsync(
               caller.User, Permission.InterruptionViewSubmitResponsible,
               ScopeOf(interruption.Task), bypassCache: true, strictScope: true, ct: ct)
               .ConfigureAwait(false);

    /// <summary>
    /// Team gate. An EMPTY <see cref="Interruption.ResponsibleTeamIds"/> means "anyone
    /// holding the respond permission" (Octopus semantics) — the orchestrator refuses
    /// to create a gate whose configured teams could not be resolved, so an empty list
    /// here always means the author left it empty, never that ids were dropped.
    /// <para>
    /// An empty list means the author left it empty: the orchestrator refuses to create
    /// a gate whose configured teams cannot be resolved, rejects an "Everyone" team as a
    /// vacuous restriction, and reads the config key case-INSENSITIVELY (a casing miss
    /// used to yield an empty list here, silently widening "these teams" to "anyone").
    /// <para>
    /// Membership is resolved by <see cref="IPermissionEvaluator.GetUserTeamIdsAsync"/>,
    /// the SAME resolver RBAC uses, so explicit members, external-IdP-group members and
    /// "Everyone" teams all count and cannot drift from permission evaluation. Deriving
    /// it here from the principal's claims would be wrong: external group memberships
    /// live on the user row (persisted at sign-in), not in the cookie.
    /// </para>
    /// <para>
    /// Self-approval is deliberately allowed (locked decision 2026-07-06): nothing here
    /// excludes the user who queued the task.
    /// </para>
    /// </summary>
    private async Task<ResponsibleVerdict> IsResponsibleAsync(
        Interruption interruption, CallerAuthorization caller, CancellationToken ct)
    {
        if (interruption.ResponsibleTeamIds.Length == 0)
        {
            return new ResponsibleVerdict(Allowed: true, BreakGlass: false);
        }
        if (caller.User is null)
        {
            return new ResponsibleVerdict(Allowed: false, BreakGlass: false);
        }

        var memberOf = await permissions.GetUserTeamIdsAsync(caller.User, ct)
            .ConfigureAwait(false);
        if (interruption.ResponsibleTeamIds.Any(memberOf.Contains))
        {
            return new ResponsibleVerdict(Allowed: true, BreakGlass: false);
        }

        // BREAK-GLASS. The responsible teams are an FK-free snapshot (deliberately, so
        // deleting a team cannot rewrite history) — which means deleting the named team
        // made the gate unanswerable by EVERYONE, including a system administrator,
        // while it kept holding the (project, environment, tenant) slot until the
        // timeout. A holder of TeamDelete alone could therefore force-fail a release
        // and block that environment. AdministerSystem may override; the override is
        // recorded AS an override in the audit trail (RecordResolutionAsync), never
        // silently — the alternative, auto-widening when no team resolves, is the
        // fail-open behaviour the gate exists to prevent.
        //
        // SCOPED to this task (WP3-b). An unscoped check would let AdministerSystem
        // granted for one Space override an author-chosen approver restriction in
        // another. NOTE this only closes the call site: PermissionEvaluator's
        // system-admin short-circuit still ignores the role assignment's own SpaceId, so
        // a Space-pinned AdministerSystem grant remains global there — pre-existing and
        // out of scope for WP3-b, flagged in the plan.
        var isAdmin = await permissions.HasPermissionAsync(
            caller.User, Permission.AdministerSystem,
            ScopeOf(interruption.Task), bypassCache: true, ct: ct).ConfigureAwait(false);

        // The break-glass flag is derived from the SAME membership read, not a second
        // one. Two independent reads could disagree: a member removed from the team in
        // between was stamped "Override=AdministerSystem" for an override they never
        // used, and an admin added to the team in between had their genuine override
        // omitted from the trail. Either way the change-control record misstated what
        // happened.
        return new ResponsibleVerdict(Allowed: isAdmin, BreakGlass: isAdmin);
    }

    /// <summary>
    /// Whether the caller may answer a gate, and whether they may do so ONLY by virtue
    /// of <see cref="Permission.AdministerSystem"/> rather than team membership — one
    /// value from one membership read, so the audit trail cannot contradict the
    /// authorization decision.
    /// </summary>
    private readonly record struct ResponsibleVerdict(bool Allowed, bool BreakGlass);

    /// <summary>
    /// The responder id + label persisted on the gate. Read from claims because it must
    /// survive the user's deletion — the same reason <c>ServerTask.CreatedByDisplay</c> is
    /// denormalized rather than joined — and through the ONE shared Core extraction, which
    /// WP3-b collapsed the five divergent copies onto.
    /// <para>
    /// A NON-INTERACTIVE response is marked as such (WP3-b). Attribution was already
    /// correct for an API-key caller — <c>ApiKeyAuthenticationHandler</c> stamps the
    /// OWNING user's name — but indistinguishable from a person clicking Approve, so a
    /// change-control trail read "Ana Anić approved, 14:02" whether a human saw the
    /// instructions or a script POSTed to <c>/api/interruptions/{id}/respond</c> with a
    /// long-lived key. The mechanism is on the identity and was simply unused.
    /// </para>
    /// </summary>
    private static (Guid? UserId, string Display) ResponderLabel(CallerAuthorization caller)
    {
        if (caller.IsSystem)
        {
            return (null, ClaimsPrincipalExtensions.SystemLabel);
        }
        var (userId, display) = caller.User.ResolveProvenance();
        var viaApiKey = string.Equals(
            caller.User?.Identity?.AuthenticationType,
            KrakenAuthSchemes.ApiKey,
            StringComparison.Ordinal);
        return (userId, viaApiKey ? $"{display} (via API key)" : display);
    }
}
