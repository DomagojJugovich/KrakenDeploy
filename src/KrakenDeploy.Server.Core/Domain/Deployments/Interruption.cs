using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Deployments;

/// <summary>
/// WP3 — one manual-intervention gate on a <see cref="ServerTask"/>: the durable
/// record that the task PAUSED at a given step, who was allowed to answer, and
/// what they decided. Octopus calls this an "Interruption"; the permissions
/// (<c>InterruptionView</c> / <c>InterruptionViewSubmitResponsible</c>) already
/// carry the name.
/// <para>
/// One row per (<see cref="TaskId"/>, <see cref="StepIndex"/>) — a step gates at
/// most once per task. The row is created in the same transaction as the
/// <c>Running → Paused</c> transition.
/// </para>
/// <para>
/// <b>Lifetime (corrected in WP3-b).</b> This is OPERATIONAL state for the gate, not
/// the durable change-control record: the FK to <c>server_tasks</c> is
/// <c>CASCADE</c>, and <c>RetentionService</c> hard-deletes tasks, so the row dies
/// with its task. The long-lived answer to "who approved this release, when, and what
/// did they write" lives in <c>audit_entries</c> under the <c>Interruption.*</c> event
/// types, which are exempt from the ordinary audit window via the
/// <c>ChangeControlAuditDays</c> retention class — so the resolution audit entry has
/// to be SELF-CONTAINED (step name, resolved responsible team names, responder
/// identity and display, decision, notes, both timestamps, and any override marker).
/// An earlier version of this comment claimed the row outlived the task; it never did,
/// and the denormalised fields below were justified on that false premise.
/// </para>
/// <para>
/// The snapshots (<see cref="ResponsibleTeamIds"/>, <see cref="ActedByDisplay"/>) keep
/// a different and better justification: they must be stable WHILE THE GATE IS OPEN,
/// so renaming or deleting a team mid-window cannot retroactively change who was
/// asked, and a 72 h wait cannot leave the panel unable to render.
/// </para>
/// </summary>
public class Interruption : Entity, ISpaceScoped
{
    /// <summary>Inherited from the parent task; pinned by the composite
    /// <c>(space_id, task_id)</c> FK.</summary>
    public Guid SpaceId { get; set; }

    public Guid TaskId { get; set; }
    public ServerTask Task { get; set; } = null!;

    /// <summary>Plan-level <c>DeploymentStepPlan.Index</c> of the gating step, so
    /// the UI can join against the frozen process snapshot and the orchestrator
    /// can record the matching <see cref="TaskStepOutcome"/>.</summary>
    public int StepIndex { get; set; }

    /// <summary>Step name at pause time (preserved so the record renders without
    /// re-resolving the snapshot).</summary>
    public required string StepName { get; set; }

    /// <summary>
    /// Operator-facing instructions, with Octostache <c>#{...}</c> placeholders
    /// ALREADY RESOLVED at pause time — the approver must read the real project /
    /// environment / release values, not the template. Null when the step
    /// configured none.
    /// </summary>
    public string? Instructions { get; set; }

    /// <summary>
    /// Snapshot of the teams authorised to answer, from the step's
    /// <c>Octopus.Action.Manual.ResponsibleTeamIds</c>. EMPTY means "anyone in the
    /// Space holding <c>InterruptionViewSubmitResponsible</c>" (Octopus semantics).
    /// <para>
    /// Deliberately a bare id snapshot rather than a join table with a real FK to
    /// <c>teams</c>: a team must be deletable without rewriting who was asked, and a
    /// real FK would force us to either block team deletion or cascade the answer away
    /// mid-window.
    /// </para>
    /// </summary>
    public Guid[] ResponsibleTeamIds { get; set; } = [];

    /// <summary>
    /// Names of <see cref="ResponsibleTeamIds"/> as they stood when the gate opened,
    /// positionally aligned with it. Empty when the gate names no teams.
    /// <para>
    /// Persisted rather than joined because the name is frequently NOT recoverable when
    /// it is needed. The break-glass path exists precisely because a named team can be
    /// DELETED while the gate waits, so resolving names at decision time would render
    /// the change-control audit entry as bare GUIDs in exactly the case a reviewer most
    /// needs to read it. It also survives the row itself: the resolution audit entry has
    /// to be self-contained, because this row is CASCADE-deleted with its task.
    /// </para>
    /// </summary>
    public string[] ResponsibleTeamNames { get; set; } = [];

    public InterruptionStatus Status { get; set; } = InterruptionStatus.Pending;

    /// <summary>
    /// When the gate auto-fails if nobody answers. Computed at pause time as
    /// <c>CreatedUtc + (step TimeoutHours ?? Engine:DefaultInterventionTimeout)</c>.
    /// <para>
    /// Always set for gates created from WP3-b on: a zero timeout (which produced a NULL
    /// here) is refused at process save and by the options validator, because the
    /// timeout sweeper skips a NULL expiry while the task keeps holding its
    /// <c>(project, environment, tenant)</c> key. Nullable only for rows written before
    /// that.
    /// </para>
    /// </summary>
    public DateTimeOffset? ExpiresUtc { get; set; }

    /// <summary>Acting user who approved/rejected. Bare <c>Guid</c> (house
    /// convention for domain→Identity refs), FK <c>SET NULL</c> so the record
    /// outlives the user. Null for a timeout, or while Pending.</summary>
    public Guid? ActedByUserId { get; set; }

    /// <summary>Denormalized responder label, captured from claims at response
    /// time. Survives user deletion — the whole point of the record. Null while
    /// Pending; a <c>"System (timeout)"</c>-style label for an expiry.</summary>
    public string? ActedByDisplay { get; set; }

    /// <summary>Responder's free-text notes. MANDATORY on reject (enforced by
    /// <c>InterruptionService</c>, not merely by the dialog), optional on
    /// approve.</summary>
    public string? Notes { get; set; }

    /// <summary>When the task paused at this gate.</summary>
    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>When the gate was answered (or expired). Null while Pending.</summary>
    public DateTimeOffset? ActedUtc { get; set; }
}

/// <summary>
/// Lifecycle of one <see cref="Interruption"/>. Stored as an int so variants are
/// additive. Every non-<see cref="Pending"/> value is resolved: the orchestrator
/// may resume the task, and the reconciler's pause arm re-signals it if the
/// wake-up was lost.
/// </summary>
public enum InterruptionStatus
{
    /// <summary>Awaiting a human decision. The task sits <c>Paused</c>.</summary>
    Pending = 0,

    /// <summary>Approved — the task resumes and continues with the gating wave.</summary>
    Approved = 1,

    /// <summary>Rejected — the task resumes only to run its
    /// <c>Failure</c>/<c>Always</c> cleanup steps, then finalises
    /// <c>Failed</c>.</summary>
    Rejected = 2,

    /// <summary>Nobody answered before <see cref="Interruption.ExpiresUtc"/>.
    /// Behaves exactly like <see cref="Rejected"/>.</summary>
    TimedOut = 3,

    /// <summary>
    /// The task went terminal (operator cancel, reconciler interrupt, a refused
    /// gate elsewhere) while this gate was still unanswered, so the question is
    /// moot. Resolved — but NOT a decision: it never resumes anything and it must
    /// never read as an approval or a refusal in the change-control trail.
    /// <para>
    /// Without this state a cancelled task kept an answerable gate: the panel still
    /// offered Approve/Reject and a response was accepted, writing an
    /// <c>InterventionApproved</c> audit row naming a real person for a deployment
    /// that was already cancelled.
    /// </para>
    /// </summary>
    Cancelled = 4,
}

/// <summary>
/// The requested manual-intervention gate does not exist.
/// <para>
/// A distinct type so the REST surface can answer 404 <em>after</em> its
/// authorization arms rather than before them. Throwing a bare
/// <see cref="InvalidOperationException"/> up front mapped a missing id to 409 while a
/// gate in an unreachable Space produced a different status — an existence oracle that
/// let a caller in one Space probe for tasks in another.
/// </para>
/// </summary>
public sealed class InterruptionNotFoundException(Guid interruptionId)
    : Exception($"Manual intervention {interruptionId} does not exist.")
{
    public Guid InterruptionId { get; } = interruptionId;
}

/// <summary>
/// Maps a resolved gate to the audit event that records it, per task kind.
/// <para>
/// Lives in Core because THREE layers emit these — the interruption service (a human
/// responded), the timeout sweeper (nobody did), and any future REST/CLI surface —
/// and a per-layer copy of the mapping would eventually drift. Kind-branched for the
/// D1 rule: a runbook run must emit <c>RunbookRun.*</c>, never <c>Deployment.*</c>,
/// because <c>SubscriptionMatcher</c> matches on the event-type string PREFIX, so a
/// reused name would leak runbook events into every <c>Deployment.*</c> subscription.
/// </para>
/// </summary>
public static class InterruptionAuditEvents
{
    /// <summary>The audit event for a resolved gate on a task of this kind.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="status"/> is <see cref="InterruptionStatus.Pending"/> — an
    /// unanswered gate has no resolution event.
    /// </exception>
    public static string For(ServerTaskKind kind, InterruptionStatus status)
    {
        var isRunbook = kind == ServerTaskKind.RunbookRun;
        return status switch
        {
            InterruptionStatus.Approved => isRunbook
                ? Audit.AuditEventType.RunbookRunInterventionApproved
                : Audit.AuditEventType.DeploymentInterventionApproved,
            InterruptionStatus.Rejected => isRunbook
                ? Audit.AuditEventType.RunbookRunInterventionRejected
                : Audit.AuditEventType.DeploymentInterventionRejected,
            InterruptionStatus.TimedOut => isRunbook
                ? Audit.AuditEventType.RunbookRunInterventionTimedOut
                : Audit.AuditEventType.DeploymentInterventionTimedOut,
            // Cancelled is resolved but is not a DECISION — the task went terminal
            // underneath it. The cancel itself is already audited
            // (Deployment.Cancelled), so emitting an intervention event here would
            // add a second, misleading change-control row.
            _ => throw new ArgumentOutOfRangeException(nameof(status), status,
                "Only Approved / Rejected / TimedOut are human decisions with a " +
                "resolution audit event."),
        };
    }

    /// <summary>Audit <c>subjectType</c> for a task of this kind — matches
    /// <c>TaskAuditVocabulary.SubjectType</c>.</summary>
    public static string SubjectType(ServerTaskKind kind)
        => kind == ServerTaskKind.RunbookRun ? "RunbookRun" : "Deployment";

    /// <summary>
    /// Every event type this class can emit — the durable CHANGE-CONTROL record of who
    /// approved or refused a production change. <c>AuditRetentionJob</c> holds these to
    /// <c>PerformanceSettings.ChangeControlAuditRetentionDays</c> (default: never purge)
    /// instead of the ordinary audit window.
    /// <para>
    /// Kept beside <see cref="For"/> so the two cannot drift: an event type added there
    /// and forgotten here would silently fall back to the 365-day window, and since the
    /// <c>interruptions</c> row is CASCADE-deleted with its task, that entry is the last
    /// copy of the approval.
    /// </para>
    /// </summary>
    public static readonly string[] ChangeControlEventTypes =
    [
        Audit.AuditEventType.DeploymentInterventionApproved,
        Audit.AuditEventType.DeploymentInterventionRejected,
        Audit.AuditEventType.DeploymentInterventionTimedOut,
        Audit.AuditEventType.RunbookRunInterventionApproved,
        Audit.AuditEventType.RunbookRunInterventionRejected,
        Audit.AuditEventType.RunbookRunInterventionTimedOut,
    ];
}

/// <summary>
/// Which resolutions let the orchestrator resume a paused task. Kept next to the
/// enum so the reconciler's pause arm, the resume claim guard and the worker's
/// rejection branch cannot drift on "what counts as answered".
/// </summary>
public static class InterruptionStatusExtensions
{
    /// <summary>True once the gate is no longer awaiting an answer, for ANY reason —
    /// including <see cref="InterruptionStatus.Cancelled"/>, where the task went
    /// terminal underneath it. Used by the guards that must not act on an open gate;
    /// use <see cref="IsDecision"/> when you mean "a human answered".</summary>
    public static bool IsResolved(this InterruptionStatus status)
        => status != InterruptionStatus.Pending;

    /// <summary>
    /// True for the three resolutions that are an actual answer to the question, and
    /// so the only ones that resume a task or belong in the change-control trail.
    /// <see cref="InterruptionStatus.Cancelled"/> is excluded: the task was already
    /// terminal, nothing resumes, and recording it as a decision would put a
    /// non-existent approval or refusal in front of an auditor.
    /// </summary>
    public static bool IsDecision(this InterruptionStatus status)
        => status is InterruptionStatus.Approved
                  or InterruptionStatus.Rejected
                  or InterruptionStatus.TimedOut;

    /// <summary>True for the two decisions that fail the task cleanly (cleanup
    /// steps still run). <see cref="InterruptionStatus.Approved"/> is the only
    /// decision that continues the deployment.</summary>
    public static bool IsRejection(this InterruptionStatus status)
        => status is InterruptionStatus.Rejected or InterruptionStatus.TimedOut;
}
