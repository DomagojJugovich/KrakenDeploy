using System.Text.Json;
using KrakenDeploy.Contracts.Adhoc;
using KrakenDeploy.Server.Core.Domain.Accounts;
using KrakenDeploy.Server.Core.Domain.Ai;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Data.Services.Ai.Adhoc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// M11.E orchestrator (commits 1–5 wired together). Owns the ad-hoc session
/// state machine end-to-end:
/// <list type="number">
///   <item><see cref="CreateSessionAsync"/> — resolves the operator's target
///         selector ONCE into the immutable <c>FrozenTargetSetJson</c>
///         (M11.E.15a) and freezes the per-session iteration cap from the
///         per-Space <c>SpaceAiSettings.AdhocMaxIterations</c> setting,
///         falling back to <c>Ai:Adhoc:MaxIterationsPerSession</c> then 5
///         (M11.E.14).</item>
///   <item><see cref="GenerateFirstIterationAsync"/> — invokes the LLM
///         generation pipeline, runs the static-analysis gate (M11.E.3), and
///         persists the proposed script as an <see cref="AdhocIteration"/> in
///         <see cref="AdhocIterationStatus.PendingApproval"/>.</item>
///   <item><see cref="ApproveIterationAsync"/> — re-runs the gate (operator may
///         have edited the script — M11.E.15c says the gate applies to every
///         iteration's final form), signs with <c>Adhoc:SigningKey</c>,
///         dispatches via <see cref="AdhocDispatcher"/>, persists per-target
///         results, calls the verdict LLM, then advances the session:
///         <list type="bullet">
///           <item><see cref="AdhocVerdict.AllSucceeded"/> /
///                 <see cref="AdhocVerdict.NoFixAvailable"/> → session closed
///                 (<see cref="AdhocSessionStatus.Closed"/>).</item>
///           <item><see cref="AdhocVerdict.ProposeFix"/> + cap reached →
///                 <see cref="AdhocSessionStatus.CapReached"/>.</item>
///           <item><see cref="AdhocVerdict.ProposeFix"/> + proposed script
///                 fails the gate → session closed with an audit entry
///                 (the LLM tried mode escalation or a forbidden cmdlet).</item>
///           <item><see cref="AdhocVerdict.ProposeFix"/> + proposed script
///                 passes the gate → iter N+1 created in
///                 <see cref="AdhocIterationStatus.PendingApproval"/>.</item>
///         </list></item>
/// </list>
/// <para>
/// Invariants enforced here (M11.E.15):
/// (a) target-set immutability — only <see cref="CreateSessionAsync"/> writes
/// the frozen set, every other path reads it; dispatcher reads it too.
/// (b) mode immutability — the gate (commit 2) rejects any mutating cmdlet
/// inside a readonly session; the verdict LLM is also told the session mode
/// in its system prompt; the proposed-fix gate-check enforces it again.
/// (c) gate on every iteration — every approval AND every proposed-fix
/// candidate goes through <see cref="AdhocScriptGate.Analyze"/>.
/// (e) signing on every iteration — every approval goes through
/// <see cref="AdhocScriptSigner.Sign"/>; the dispatcher refuses to dispatch
/// an iteration without a signature.
/// </para>
/// </summary>
public sealed class AdhocSessionService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    SettingsService settings,
    AdhocGenerationService generation,
    AdhocVerdictService verdict,
    AdhocSigningKeyProvider signingKey,
    IAdhocDispatcher dispatcher,
    IAccountContext accountContext,
    IAuditLog auditLog,
    IConfiguration config,
    TimeProvider clock,
    ILogger<AdhocSessionService> logger)
{
    private const string MaxIterationsConfigKey = "Ai:Adhoc:MaxIterationsPerSession";

    /// <summary>
    /// Resolves <paramref name="targetIds"/> into the immutable frozen set,
    /// freezes the iteration cap from the current Space's
    /// <c>SpaceAiSettings.AdhocMaxIterations</c> (fallback config, then 5),
    /// and persists the session in <see cref="AdhocSessionStatus.Active"/>.
    /// Returns the new session id.
    /// </summary>
    public async Task<Guid> CreateSessionAsync(
        string prompt,
        AdhocMode mode,
        IReadOnlyList<Guid> targetIds,
        Guid createdByUserId,
        string createdByDisplay,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentNullException.ThrowIfNull(targetIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(createdByDisplay);

        if (targetIds.Count == 0)
        {
            throw new ArgumentException(
                "Cannot create an ad-hoc session with an empty target set — the " +
                "session needs at least one target to dispatch against.",
                nameof(targetIds));
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var maxIterations = await ReadMaxIterationsAsync(db, ct).ConfigureAwait(false);

        var session = new AdhocSession
        {
            Prompt              = prompt,
            Mode                = mode,
            Status              = AdhocSessionStatus.Active,
            FrozenTargetSetJson = JsonSerializer.Serialize(targetIds),
            CreatedByUserId     = createdByUserId,
            CreatedByDisplay    = createdByDisplay,
            MaxIterations       = maxIterations,
        };
        db.AdhocSessions.Add(session);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation(
            "Adhoc session {SessionId} created by {User} ({TargetCount} targets, mode={Mode}, " +
            "maxIterations={Max}).",
            session.Id, createdByDisplay, targetIds.Count, mode, maxIterations);

        return session.Id;
    }

    /// <summary>
    /// Calls the LLM generation pipeline, gates the proposed script, and
    /// persists iteration 1 in <see cref="AdhocIterationStatus.PendingApproval"/>.
    /// </summary>
    /// <exception cref="AdhocFeatureUnavailableException">AI provider /
    /// feature flag / budget unavailable.</exception>
    /// <exception cref="AdhocGateRejectedException">The LLM-generated script
    /// failed the static-analysis gate (mode escalation or forbidden cmdlet).
    /// The iteration is NOT created; the operator can retry with a clearer
    /// prompt or change mode.</exception>
    public async Task<Guid> GenerateFirstIterationAsync(Guid sessionId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var session = await LoadActiveSessionAsync(db, sessionId, ct).ConfigureAwait(false);

        if (session.Iterations.Count > 0)
        {
            throw new InvalidOperationException(
                $"Session {sessionId} already has iterations — generate-first " +
                "is only valid on a fresh session.");
        }

        var targets = await ResolveFrozenTargetsAsync(db, session, ct).ConfigureAwait(false);

        var generated = await generation.GenerateAsync(
            session, targets, sensitiveValues: null, ct).ConfigureAwait(false);

        var gate = AdhocScriptGate.Analyze(generated.GeneratedScript, session.Mode);
        if (!gate.IsAllowed)
        {
            logger.LogWarning(
                "Adhoc session {SessionId}: gate rejected iter 1's generated script — {Summary}",
                session.Id, gate.Summary);
            await auditLog.RecordAsync(
                AuditEventType.AdhocGateRejected,
                subjectType: "AdhocSession",
                subjectId:   session.Id.ToString(),
                details:     $"Iter=1, Summary={gate.Summary}",
                ct: ct).ConfigureAwait(false);
            throw new AdhocGateRejectedException(gate);
        }

        var iter = new AdhocIteration
        {
            SpaceId             = session.SpaceId,
            SessionId           = session.Id,
            IterNumber          = 1,
            CreatedUtc          = clock.GetUtcNow(),
            GeneratedScript     = generated.GeneratedScript,
            Description         = generated.Description,
            RiskAssessment      = generated.RiskAssessment,
            ExpectedOutputShape = generated.ExpectedOutputShape,
            RequiresMutation    = generated.RequiresMutation,
            Status              = AdhocIterationStatus.PendingApproval,
        };
        db.AdhocIterations.Add(iter);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation(
            "Adhoc session {SessionId} iter 1 generated; awaiting approval.", session.Id);

        return iter.Id;
    }

    /// <summary>
    /// Operator-approval path: re-gates, then either records the FIRST of two
    /// approvals (M11.E.11 two-person mode, when required) and stops, or — for a
    /// single-approver session or the SECOND approval — signs, dispatches,
    /// persists results, runs the verdict LLM, and advances the session.
    /// <para>
    /// Two-person is required when the Space has
    /// <c>SpaceAiSettings.AdhocTwoPersonApproval</c> on AND the session is
    /// Mutating OR its frozen target set contains a Production-risk target
    /// (max-risk, evaluated fresh against targets' current classifications). The
    /// second approver MUST differ from the first approver and from the session
    /// creator. Edits are only allowed at first approval — the second approver
    /// vets the exact script the first one approved.
    /// </para>
    /// </summary>
    public async Task<AdhocApprovalOutcome> ApproveIterationAsync(
        Guid sessionId,
        Guid iterationId,
        Guid approvedByUserId,
        string approvedByDisplay,
        string? editedScript,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedByDisplay);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var session = await LoadActiveSessionAsync(db, sessionId, ct).ConfigureAwait(false);
        var iter = session.Iterations.SingleOrDefault(i => i.Id == iterationId)
            ?? throw new InvalidOperationException(
                $"Iteration {iterationId} not found in session {sessionId}.");

        if (iter.Status is not (AdhocIterationStatus.PendingApproval
                                or AdhocIterationStatus.PendingSecondApproval))
        {
            throw new InvalidOperationException(
                $"Iteration {iter.IterNumber} of session {sessionId} is " +
                $"{iter.Status}; only PendingApproval / PendingSecondApproval " +
                "iterations can be approved.");
        }

        // Operator edit replaces the LLM-generated script — but only at FIRST
        // approval; the second approver must vet the exact text the first
        // approver saw, so an edit there would invalidate the first approval.
        if (!string.IsNullOrEmpty(editedScript))
        {
            if (iter.Status != AdhocIterationStatus.PendingApproval)
            {
                throw new InvalidOperationException(
                    "The script cannot be edited at second approval — that would " +
                    "invalidate the first approver's review.");
            }
            iter.GeneratedScript = editedScript;
        }

        // Re-gate (M11.E.15c). The gate is the security contract — applies to
        // every iteration's final script, on every approval, regardless of who
        // wrote it.
        var gate = AdhocScriptGate.Analyze(iter.GeneratedScript, session.Mode);
        if (!gate.IsAllowed)
        {
            await auditLog.RecordAsync(
                AuditEventType.AdhocGateRejected,
                subjectType: "AdhocSession",
                subjectId:   session.Id.ToString(),
                details:     $"Iter={iter.IterNumber}, OnApproval=true, Summary={gate.Summary}",
                ct: ct).ConfigureAwait(false);
            throw new AdhocGateRejectedException(gate);
        }

        // Two-person policy (M11.E.11), evaluated fresh at each approval so a
        // mid-session reclassification or flag change takes effect immediately.
        var requiresTwoPerson = await RequiresTwoPersonAsync(db, session, ct).ConfigureAwait(false);

        if (requiresTwoPerson && iter.Status == AdhocIterationStatus.PendingApproval)
        {
            // Record the first approval and stop — no signing, no dispatch.
            iter.FirstApprovedByUserId  = approvedByUserId;
            iter.FirstApprovedByDisplay = approvedByDisplay;
            iter.FirstApprovedAtUtc     = clock.GetUtcNow();
            iter.Status                 = AdhocIterationStatus.PendingSecondApproval;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            await auditLog.RecordAsync(
                AuditEventType.AdhocIterationApproved,
                subjectType: "AdhocSession",
                subjectId:   session.Id.ToString(),
                details:     $"Iter={iter.IterNumber}, Stage=FirstApproval, By={approvedByDisplay}",
                ct: ct).ConfigureAwait(false);

            return new AdhocApprovalOutcome(
                SessionStatus: session.Status,
                NextIterationId: null,
                Verdict: AdhocVerdict.Pending,
                AwaitingSecondApproval: true);
        }

        // Final approval — single-approver session, or the SECOND of two.
        // Enforce distinctness for the second approval.
        if (iter.Status == AdhocIterationStatus.PendingSecondApproval)
        {
            if (approvedByUserId == iter.FirstApprovedByUserId)
            {
                throw new InvalidOperationException(
                    "The second approver must be a different person from the first approver.");
            }
            if (approvedByUserId == session.CreatedByUserId)
            {
                throw new InvalidOperationException(
                    "The second approver must not be the session creator.");
            }
        }

        // Sign (M11.E.6) and stamp the final approval.
        var sig = AdhocScriptSigner.Sign(
            session.Id, iter.IterNumber, iter.GeneratedScript, signingKey.GetPrivateKey());
        var stage = iter.FirstApprovedByUserId is null ? "Approval" : "SecondApproval";
        iter.ScriptSignature   = sig;
        iter.ApprovedByUserId  = approvedByUserId;
        iter.ApprovedByDisplay = approvedByDisplay;
        iter.ApprovedAtUtc     = clock.GetUtcNow();
        iter.Status            = AdhocIterationStatus.Approved;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await auditLog.RecordAsync(
            AuditEventType.AdhocIterationApproved,
            subjectType: "AdhocSession",
            subjectId:   session.Id.ToString(),
            details:     $"Iter={iter.IterNumber}, Stage={stage}, ApprovedBy={approvedByDisplay}",
            ct: ct).ConfigureAwait(false);

        // Dispatch (M11.E.7).
        iter.Status = AdhocIterationStatus.Executing;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Pass the dispatching account so the dispatcher can fail-closed against a target
        // whose live connection belongs to a different account (Guid.Empty = single-instance).
        var dispatchAccountId = accountContext.IsResolved ? accountContext.CurrentAccountId : Guid.Empty;
        // F5 (locked decision P5) — the AI ad-hoc flow is READ-always: an LLM-generated
        // script that passed the AST gate and an operator's approval takes the SHARED
        // side of every agent's machine gate, so it co-runs with other shared work and
        // never excludes anything. F2 stamped each target's own
        // AllowParallelTaskExecution here, which once the flag stopped meaning "bypass"
        // made a serial target turn a read-only diagnostic into an exclusive holder.
        // WP16's script console is where the operator's per-run choice will flow in.
        var results = await dispatcher
            .DispatchAsync(session, iter, dispatchAccountId,
                allowParallelTaskExecution: true, ct)
            .ConfigureAwait(false);

        iter.ResultsJson = JsonSerializer.Serialize(results);
        iter.Status      = AdhocIterationStatus.Completed;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Verdict (M11.E.13). The verdict service takes the wire-shape
        // AdhocScriptResult list — unwrap the server-side projection.
        var wireResults = results.Select(r => r.Result).ToList();
        var verdictResult = await verdict.EvaluateAsync(session, iter, wireResults, ct).ConfigureAwait(false);
        iter.Verdict   = AdhocVerdictService.ParseVerdict(verdictResult.Verdict);
        iter.Narrative = Trim(verdictResult.Narrative, 4000);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Advance.
        return await AdvanceAfterVerdictAsync(db, session, iter, verdictResult, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Operator rejects this iteration's script. Session stays Active;
    /// no new iteration is opened automatically (operator can re-prompt or
    /// stop the session).</summary>
    public async Task RejectIterationAsync(
        Guid sessionId, Guid iterationId, string rejectedByDisplay, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rejectedByDisplay);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var session = await LoadActiveSessionAsync(db, sessionId, ct).ConfigureAwait(false);
        var iter = session.Iterations.SingleOrDefault(i => i.Id == iterationId)
            ?? throw new InvalidOperationException(
                $"Iteration {iterationId} not found in session {sessionId}.");
        if (iter.Status is not (AdhocIterationStatus.PendingApproval
                                or AdhocIterationStatus.PendingSecondApproval))
        {
            throw new InvalidOperationException(
                $"Iteration {iter.IterNumber} of session {sessionId} is " +
                $"{iter.Status}; only PendingApproval / PendingSecondApproval " +
                "iterations can be rejected.");
        }

        iter.Status = AdhocIterationStatus.Rejected;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await auditLog.RecordAsync(
            AuditEventType.AdhocIterationRejected,
            subjectType: "AdhocSession",
            subjectId:   session.Id.ToString(),
            details:     $"Iter={iter.IterNumber}, RejectedBy={rejectedByDisplay}",
            ct: ct).ConfigureAwait(false);
    }

    /// <summary>Operator closes the session as resolved (M11.E.16).</summary>
    public Task MarkResolvedAsync(Guid sessionId, string byDisplay, CancellationToken ct)
        => CloseSessionAsync(sessionId, AdhocSessionStatus.Closed, byDisplay,
            AuditEventType.AdhocSessionClosed, "resolved", ct);

    /// <summary>Operator stops the session early (M11.E.16).</summary>
    public Task StopSessionAsync(Guid sessionId, string byDisplay, CancellationToken ct)
        => CloseSessionAsync(sessionId, AdhocSessionStatus.OperatorStopped, byDisplay,
            AuditEventType.AdhocSessionStopped, "operator-stopped", ct);

    // ── Queries (for the /adhoc UI page) ────────────────────────────────────

    /// <summary>Newest-first list of this Space's sessions, with iteration
    /// counts attached for the list page's per-row summary.</summary>
    public async Task<IReadOnlyList<AdhocSessionListItem>> ListSessionsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.AdhocSessions
            .OrderByDescending(s => s.CreatedUtc)
            .Select(s => new AdhocSessionListItem(
                s.Id, s.Prompt, s.Mode, s.Status, s.CreatedByDisplay,
                s.CreatedUtc, s.Iterations.Count, s.MaxIterations))
            .ToListAsync(ct).ConfigureAwait(false);
        return rows;
    }

    /// <summary>Full session aggregate (with iterations) for the detail page.
    /// Returns <c>null</c> when the session id is unknown or out of the
    /// caller's Space scope (the query filter blocks cross-Space reads).</summary>
    public async Task<AdhocSession?> GetSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.AdhocSessions
            .Include(s => s.Iterations)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            .ConfigureAwait(false);
    }

    // ── Internals ───────────────────────────────────────────────────────────

    private async Task<AdhocApprovalOutcome> AdvanceAfterVerdictAsync(
        KrakenDbContext db,
        AdhocSession session,
        AdhocIteration completedIter,
        IterationVerdict verdictResult,
        CancellationToken ct)
    {
        switch (completedIter.Verdict)
        {
            case AdhocVerdict.AllSucceeded:
                session.Status = AdhocSessionStatus.Closed;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await auditLog.RecordAsync(
                    AuditEventType.AdhocSessionClosed,
                    subjectType: "AdhocSession",
                    subjectId:   session.Id.ToString(),
                    details:     $"Iters={completedIter.IterNumber}, Verdict=AllSucceeded",
                    ct: ct).ConfigureAwait(false);
                return new AdhocApprovalOutcome(
                    SessionStatus: session.Status,
                    NextIterationId: null,
                    Verdict: completedIter.Verdict);

            case AdhocVerdict.NoFixAvailable:
                session.Status = AdhocSessionStatus.Closed;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await auditLog.RecordAsync(
                    AuditEventType.AdhocSessionClosed,
                    subjectType: "AdhocSession",
                    subjectId:   session.Id.ToString(),
                    details:     $"Iters={completedIter.IterNumber}, Verdict=NoFixAvailable",
                    ct: ct).ConfigureAwait(false);
                return new AdhocApprovalOutcome(
                    SessionStatus: session.Status,
                    NextIterationId: null,
                    Verdict: completedIter.Verdict);

            case AdhocVerdict.ProposeFix:
                // Cap check first — if we're at the limit, don't even try the
                // next gate-check (it'd be wasted work).
                if (completedIter.IterNumber >= session.MaxIterations)
                {
                    session.Status = AdhocSessionStatus.CapReached;
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    await auditLog.RecordAsync(
                        AuditEventType.AdhocSessionCapReached,
                        subjectType: "AdhocSession",
                        subjectId:   session.Id.ToString(),
                        details:     $"MaxIterations={session.MaxIterations} reached; " +
                                     "manual intervention required.",
                        ct: ct).ConfigureAwait(false);
                    logger.LogWarning(
                        "Adhoc session {SessionId} hit iteration cap ({Max}); manual " +
                        "intervention required.", session.Id, session.MaxIterations);
                    return new AdhocApprovalOutcome(
                        SessionStatus: session.Status,
                        NextIterationId: null,
                        Verdict: completedIter.Verdict);
                }

                var proposed = verdictResult.ProposedScript;
                if (string.IsNullOrWhiteSpace(proposed))
                {
                    // LLM said ProposeFix but didn't supply a script — treat as
                    // NoFixAvailable to close safely.
                    session.Status = AdhocSessionStatus.Closed;
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    logger.LogWarning(
                        "Adhoc session {SessionId}: verdict ProposeFix but no script " +
                        "supplied; closing.", session.Id);
                    return new AdhocApprovalOutcome(
                        SessionStatus: session.Status,
                        NextIterationId: null,
                        Verdict: completedIter.Verdict);
                }

                // Gate the proposed fix BEFORE creating the iter row — saves
                // an "approval dialog for a script the gate will reject"
                // round-trip and gives a clean audit entry.
                var gate = AdhocScriptGate.Analyze(proposed, session.Mode);
                if (!gate.IsAllowed)
                {
                    session.Status = AdhocSessionStatus.Closed;
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    await auditLog.RecordAsync(
                        AuditEventType.AdhocGateRejected,
                        subjectType: "AdhocSession",
                        subjectId:   session.Id.ToString(),
                        details:     $"Iter={completedIter.IterNumber + 1} (proposed fix), " +
                                     $"Summary={gate.Summary}",
                        ct: ct).ConfigureAwait(false);
                    logger.LogWarning(
                        "Adhoc session {SessionId}: verdict's proposed fix failed the " +
                        "gate ({Summary}); closing.", session.Id, gate.Summary);
                    return new AdhocApprovalOutcome(
                        SessionStatus: session.Status,
                        NextIterationId: null,
                        Verdict: completedIter.Verdict);
                }

                var nextIter = new AdhocIteration
                {
                    SpaceId             = session.SpaceId,
                    SessionId           = session.Id,
                    IterNumber          = completedIter.IterNumber + 1,
                    CreatedUtc          = clock.GetUtcNow(),
                    GeneratedScript     = proposed,
                    Description         = verdictResult.ProposedScriptDescription ?? string.Empty,
                    RiskAssessment      = verdictResult.RiskAssessment ?? string.Empty,
                    ExpectedOutputShape = string.Empty,
                    RequiresMutation    = session.Mode == AdhocMode.Mutating,
                    Status              = AdhocIterationStatus.PendingApproval,
                };
                db.AdhocIterations.Add(nextIter);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return new AdhocApprovalOutcome(
                    SessionStatus: session.Status,
                    NextIterationId: nextIter.Id,
                    Verdict: completedIter.Verdict);

            default:
                // Pending — shouldn't happen after EvaluateAsync, but fail
                // closed and don't advance.
                logger.LogError(
                    "Adhoc session {SessionId} iter {Iter}: verdict remained " +
                    "{Verdict} after evaluation; not advancing.",
                    session.Id, completedIter.IterNumber, completedIter.Verdict);
                return new AdhocApprovalOutcome(
                    SessionStatus: session.Status,
                    NextIterationId: null,
                    Verdict: completedIter.Verdict);
        }
    }

    private async Task CloseSessionAsync(
        Guid sessionId,
        AdhocSessionStatus targetStatus,
        string byDisplay,
        string auditEventType,
        string reason,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var session = await db.AdhocSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Session {sessionId} not found.");

        if (session.Status != AdhocSessionStatus.Active)
        {
            // Idempotent — already closed.
            return;
        }

        session.Status = targetStatus;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await auditLog.RecordAsync(
            auditEventType,
            subjectType: "AdhocSession",
            subjectId:   session.Id.ToString(),
            details:     $"By={byDisplay}, Reason={reason}",
            ct: ct).ConfigureAwait(false);
    }

    private static async Task<AdhocSession> LoadActiveSessionAsync(
        KrakenDbContext db, Guid sessionId, CancellationToken ct)
    {
        var session = await db.AdhocSessions
            .Include(s => s.Iterations)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Session {sessionId} not found.");
        if (session.Status != AdhocSessionStatus.Active)
        {
            throw new InvalidOperationException(
                $"Session {sessionId} is {session.Status}; cannot modify a closed session.");
        }
        return session;
    }

    private static async Task<IReadOnlyList<DeploymentTarget>> ResolveFrozenTargetsAsync(
        KrakenDbContext db, AdhocSession session, CancellationToken ct)
    {
        var ids = JsonSerializer.Deserialize<List<Guid>>(session.FrozenTargetSetJson) ?? [];
        if (ids.Count == 0) { return []; }
        return await db.DeploymentTargets
            .Where(t => ids.Contains(t.Id))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// True when this approval must be two-person (M11.E.11): the Space has the
    /// opt-in on AND the session is Mutating OR its frozen target set's max risk
    /// is Production. Evaluated fresh per approval.
    /// </summary>
    private async Task<bool> RequiresTwoPersonAsync(
        KrakenDbContext db, AdhocSession session, CancellationToken ct)
    {
        var spaceId = db.CurrentSpaceId;
        var doc = spaceId == Guid.Empty
            ? null
            : await settings.TryGetAsync<SpaceAiSettings>(spaceId, ct).ConfigureAwait(false);
        var enabled = doc?.AdhocTwoPersonApproval ?? false;
        if (!enabled) { return false; }
        if (session.Mode == AdhocMode.Mutating) { return true; }

        var maxRisk = await ComputeMaxRiskAsync(db, session, ct).ConfigureAwait(false);
        return maxRisk == TargetRiskLevel.Production;
    }

    /// <summary>
    /// Effective risk of a session = the MAXIMUM <see cref="TargetRiskLevel"/>
    /// across its frozen target set (one Production box makes the whole session
    /// Production-risk). A since-deleted / unresolvable target counts as
    /// Production (fail-safe), as does an empty set.
    /// </summary>
    private static async Task<TargetRiskLevel> ComputeMaxRiskAsync(
        KrakenDbContext db, AdhocSession session, CancellationToken ct)
    {
        var ids = JsonSerializer.Deserialize<List<Guid>>(session.FrozenTargetSetJson) ?? [];
        if (ids.Count == 0) { return TargetRiskLevel.Production; }

        var levels = await db.DeploymentTargets
            .Where(t => ids.Contains(t.Id))
            .Select(t => t.RiskLevel)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Any frozen target that can no longer be resolved counts as Production.
        if (levels.Count < ids.Count) { return TargetRiskLevel.Production; }
        return levels.Count == 0 ? TargetRiskLevel.Production : levels.Max();
    }

    /// <summary>
    /// Effective (max) risk across the session's frozen target set, for UI
    /// display (louder approval banner on Production). A since-deleted target
    /// counts as Production (fail-safe).
    /// </summary>
    public async Task<TargetRiskLevel> GetEffectiveRiskAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var session = await db.AdhocSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            .ConfigureAwait(false);
        return session is null
            ? TargetRiskLevel.Production
            : await ComputeMaxRiskAsync(db, session, ct).ConfigureAwait(false);
    }

    private async Task<int> ReadMaxIterationsAsync(KrakenDbContext db, CancellationToken ct)
    {
        // Per-Space override wins (SaaS — every Space tunes its own cap). Read the
        // current Space's AI settings document (null when the Space has never
        // configured AI). Fall back to the deployment-wide config default, then a
        // hard default of 5.
        var spaceId = db.CurrentSpaceId;
        var doc = spaceId == Guid.Empty
            ? null
            : await settings.TryGetAsync<SpaceAiSettings>(spaceId, ct).ConfigureAwait(false);
        if (doc is { AdhocMaxIterations: > 0 }) { return doc.AdhocMaxIterations; }

        if (int.TryParse(config[MaxIterationsConfigKey], out var v) && v > 0) { return v; }
        return 5;
    }

    private static string Trim(string s, int max)
        => s.Length <= max ? s : s[..max];
}

/// <summary>Compact projection used by the sessions list page.</summary>
public sealed record AdhocSessionListItem(
    Guid Id,
    string Prompt,
    AdhocMode Mode,
    AdhocSessionStatus Status,
    string CreatedByDisplay,
    DateTimeOffset CreatedUtc,
    int IterationCount,
    int MaxIterations);

/// <summary>Outcome returned by <see cref="AdhocSessionService.ApproveIterationAsync"/>.</summary>
/// <param name="AwaitingSecondApproval">M11.E.11 — true when this call recorded
/// the FIRST of two required approvals; the iteration is now
/// <see cref="AdhocIterationStatus.PendingSecondApproval"/> and nothing has been
/// signed or dispatched yet. A second, distinct approver must approve.</param>
public sealed record AdhocApprovalOutcome(
    AdhocSessionStatus SessionStatus,
    Guid? NextIterationId,
    AdhocVerdict Verdict,
    bool AwaitingSecondApproval = false);

/// <summary>
/// Thrown by <see cref="AdhocSessionService"/> when the static-analysis gate
/// (M11.E.3) rejects a script — either the LLM's first generation or a
/// proposed fix or an operator-edited script at approval time. Carries the
/// gate result so the caller can surface the violation list to the operator.
/// </summary>
public sealed class AdhocGateRejectedException : Exception
{
    public AdhocScriptGateResult Result { get; }
    public AdhocGateRejectedException(AdhocScriptGateResult result)
        : base($"Script rejected by static-analysis gate: {result.Summary}")
    {
        Result = result;
    }
}
