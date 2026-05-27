using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Ai;

/// <summary>
/// M11.E — one ad-hoc agent-action session: an operator's natural-language
/// request resolved against a frozen target set, then driven through one or
/// more operator-approved iterations (the LLM generates a PowerShell script,
/// the static-analysis gate vets it, the operator approves, the server signs
/// it, agents verify + run it, results stream back; on partial failure the
/// LLM may propose a fix for the next iteration).
/// <para>
/// <strong>Frozen target set (M11.E.15a):</strong>
/// <see cref="FrozenTargetSetJson"/> is resolved ONCE at session creation
/// (from the operator's role/tag/explicit-id selector) and is immutable for
/// the session's entire lifetime. Every iteration dispatches to exactly this
/// set; neither the operator nor the LLM can change it. Operators who want a
/// different blast radius start a fresh session.
/// </para>
/// <para>
/// <strong>Mode immutability (M11.E.15b):</strong> a session started
/// <see cref="AdhocMode.Readonly"/> can never have an iteration propose a
/// <see cref="AdhocMode.Mutating"/> script — the static-analysis gate rejects
/// mode escalation on every iteration.
/// </para>
/// </summary>
public class AdhocSession : AuditableEntity, ISpaceScoped
{
    /// <summary>Owning Space. Auto-stamped by <c>SpaceScopingInterceptor</c>.</summary>
    public Guid SpaceId { get; set; }

    /// <summary>The operator's original natural-language request. Fed into
    /// the generation prompt for iteration 1 and carried (as
    /// <c>originalPrompt</c>) into every iteration's verdict call.</summary>
    public string Prompt { get; set; } = string.Empty;

    /// <summary>Read-only vs mutating. Set once at creation; immutable for
    /// the session lifetime (M11.E.15b). Stored as int so adding a variant
    /// stays additive.</summary>
    public AdhocMode Mode { get; set; } = AdhocMode.Readonly;

    /// <summary>
    /// JSON array of resolved target ids — the frozen blast radius
    /// (<c>["guid", "guid", …]</c>). Set ONCE on creation from the
    /// operator's selector; never mutated. The dispatcher rejects any
    /// target not in this set (M11.E.15a / M11.E.17). Stored as jsonb.
    /// </summary>
    public string FrozenTargetSetJson { get; set; } = "[]";

    /// <summary>Lifecycle state. Drives whether new iterations can be
    /// opened. Stored as int.</summary>
    public AdhocSessionStatus Status { get; set; } = AdhocSessionStatus.Active;

    /// <summary>User id of the operator who created the session. Forensic
    /// linkage; <see cref="CreatedByDisplay"/> is the denormalised label.</summary>
    public Guid CreatedByUserId { get; set; }

    /// <summary>Denormalised display name (email) of the creator, captured
    /// at creation so the audit trail stays readable after a rename/delete.</summary>
    public string CreatedByDisplay { get; set; } = string.Empty;

    /// <summary>Per-session iteration cap (M11.E.14). Defaulted from the
    /// per-Space <c>Ai:Adhoc:MaxIterationsPerSession</c> (fallback 5) at
    /// creation. When the iteration count reaches this, the session
    /// auto-closes with <see cref="AdhocSessionStatus.CapReached"/>.</summary>
    public int MaxIterations { get; set; } = 5;

    /// <summary>The session's turns, one row per iteration, ordered by
    /// <see cref="AdhocIteration.IterNumber"/>. Cascade-deleted with the
    /// session.</summary>
    public List<AdhocIteration> Iterations { get; set; } = [];

    /// <summary>True while the session can still open another iteration:
    /// it's Active and hasn't reached the cap.</summary>
    public bool CanIterate
        => Status == AdhocSessionStatus.Active && Iterations.Count < MaxIterations;
}

/// <summary>Read-only vs mutating ad-hoc action. Stored as int (additive).</summary>
public enum AdhocMode
{
    /// <summary>Only <c>Get-*</c> / <c>Test-*</c> / <c>Measure-*</c> cmdlets
    /// are allowed; the gate rejects anything that could change state.</summary>
    Readonly = 0,

    /// <summary>State-changing scripts permitted (still gated against the
    /// forbidden-cmdlet list).</summary>
    Mutating = 1,
}

/// <summary>Lifecycle of an <see cref="AdhocSession"/>. Stored as int (additive).</summary>
public enum AdhocSessionStatus
{
    /// <summary>Accepting iterations.</summary>
    Active = 0,

    /// <summary>Operator marked the session resolved (M11.E.16
    /// "Mark resolved").</summary>
    Closed = 1,

    /// <summary>Auto-closed because the iteration cap was hit without
    /// resolution (M11.E.14).</summary>
    CapReached = 2,

    /// <summary>Auto-closed because the Space's monthly AI budget was
    /// exceeded mid-session (M11.E.15d).</summary>
    BudgetExceeded = 3,

    /// <summary>Operator stopped the session (M11.E.16 "Stop session").</summary>
    OperatorStopped = 4,
}
