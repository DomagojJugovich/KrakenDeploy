using System.Text.Json;
using KrakenDeploy.Contracts.Adhoc;
using KrakenDeploy.Server.Core.Domain.Ai;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data;
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
    AdhocGenerationService generation,
    AdhocVerdictService verdict,
    AdhocSigningKeyProvider signingKey,
    IAdhocDispatcher dispatcher,
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
    /// Operator-approval path: re-gates, signs, dispatches, persists results,
    /// runs the verdict LLM, and advances the session. Idempotent on a
    /// terminal iteration (already Completed/Rejected) — throws so the UI
    /// double-click can't double-dispatch.
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

        if (iter.Status != AdhocIterationStatus.PendingApproval)
        {
            throw new InvalidOperationException(
                $"Iteration {iter.IterNumber} of session {sessionId} is " +
                $"{iter.Status}; only PendingApproval iterations can be approved.");
        }

        // Operator edit replaces the LLM-generated script — the gate runs
        // against the FINAL form, not the original.
        if (!string.IsNullOrEmpty(editedScript))
        {
            iter.GeneratedScript = editedScript;
        }

        // Re-gate (M11.E.15c). The gate is the security contract — applies to
        // every iteration's final script, regardless of who wrote it.
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

        // Sign (M11.E.6) and stamp approval.
        var sig = AdhocScriptSigner.Sign(
            session.Id, iter.IterNumber, iter.GeneratedScript, signingKey.GetPrivateKey());
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
            details:     $"Iter={iter.IterNumber}, ApprovedBy={approvedByDisplay}",
            ct: ct).ConfigureAwait(false);

        // Dispatch (M11.E.7).
        iter.Status = AdhocIterationStatus.Executing;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var results = await dispatcher.DispatchAsync(session, iter, ct).ConfigureAwait(false);

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
        if (iter.Status != AdhocIterationStatus.PendingApproval)
        {
            throw new InvalidOperationException(
                $"Iteration {iter.IterNumber} of session {sessionId} is " +
                $"{iter.Status}; only PendingApproval iterations can be rejected.");
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

    private async Task<int> ReadMaxIterationsAsync(KrakenDbContext db, CancellationToken ct)
    {
        // Per-Space override wins (SaaS — every Space tunes its own cap). The
        // SpaceAiSettings row is space-filtered by the global query filter, so
        // FirstOrDefault returns the current Space's row (or null when a Space
        // has never configured AI). Fall back to the deployment-wide config
        // default, then a hard default of 5.
        var perSpace = await db.SpaceAiSettings
            .AsNoTracking()
            .Select(s => (int?)s.AdhocMaxIterations)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (perSpace is > 0) { return perSpace.Value; }

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
public sealed record AdhocApprovalOutcome(
    AdhocSessionStatus SessionStatus,
    Guid? NextIterationId,
    AdhocVerdict Verdict);

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
