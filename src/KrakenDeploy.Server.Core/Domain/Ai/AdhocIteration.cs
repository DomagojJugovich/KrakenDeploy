using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Ai;

/// <summary>
/// M11.E — one turn within an <see cref="AdhocSession"/>. Each iteration is a
/// fresh approval gate over the session's frozen target set: the LLM proposes
/// a script, the operator approves (optionally after editing), the server
/// signs it, agents verify + run it, results land in
/// <see cref="ResultsJson"/>, and a verdict LLM call records
/// <see cref="Verdict"/> + <see cref="Narrative"/>. A <c>ProposeFix</c>
/// verdict opens the next iteration.
/// <para>
/// Not <c>ISpaceScoped</c> by design — it reaches its Space transitively via
/// <see cref="SessionId"/>, like other child rows (e.g. deployment log
/// entries). Only the <see cref="AdhocSession"/> aggregate carries the direct
/// Space FK.
/// </para>
/// </summary>
public class AdhocIteration : Entity
{
    /// <summary>FK to the owning <see cref="AdhocSession"/>.</summary>
    public Guid SessionId { get; set; }

    /// <summary>1-based turn number within the session. Unique per session.</summary>
    public int IterNumber { get; set; }

    /// <summary>When this iteration row was created (the script was
    /// proposed). Explicit (not <c>AuditableEntity</c>) to avoid EF
    /// auto-audit noise on the frequent status/results writes.</summary>
    public DateTimeOffset CreatedUtc { get; set; }

    // ── Generation output (M11.E.2) ─────────────────────────────────────────

    /// <summary>The PowerShell the LLM generated (or the operator-edited
    /// version, post "Edit and approve"). This exact text is what gets
    /// signed + dispatched.</summary>
    public string GeneratedScript { get; set; } = string.Empty;

    /// <summary>LLM's one-line description of what the script does. Shown in
    /// the approval dialog.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>LLM's risk assessment. Shown in the approval dialog;
    /// production-target mutating sessions get a louder banner.</summary>
    public string RiskAssessment { get; set; } = string.Empty;

    /// <summary>LLM's description of the output the script is expected to
    /// produce — helps the operator sanity-check before approving.</summary>
    public string ExpectedOutputShape { get; set; } = string.Empty;

    /// <summary>Whether the LLM flagged the script as state-changing. A
    /// <c>true</c> here in a <see cref="AdhocMode.Readonly"/> session is a
    /// mode-escalation attempt; the gate rejects it regardless.</summary>
    public bool RequiresMutation { get; set; }

    // ── Approval + signing (M11.E.5 / M11.E.6) ──────────────────────────────

    /// <summary>Lifecycle of this turn. Stored as int (additive).</summary>
    public AdhocIterationStatus Status { get; set; } = AdhocIterationStatus.PendingApproval;

    /// <summary>Base64 RSA-SHA256 signature over the approved script,
    /// produced with the <c>Adhoc:SigningKey</c> on approval. The agent
    /// re-verifies this before execution; a mismatch is rejected loudly.
    /// Null until approved.</summary>
    public string? ScriptSignature { get; set; }

    /// <summary>User id of the operator who approved this iteration. Null
    /// until approved.</summary>
    public Guid? ApprovedByUserId { get; set; }

    /// <summary>Denormalised display name of the approver, captured at
    /// approval time. Null until approved.</summary>
    public string? ApprovedByDisplay { get; set; }

    /// <summary>UTC approval timestamp. Null until approved.</summary>
    public DateTimeOffset? ApprovedAtUtc { get; set; }

    // ── Execution results (M11.E.7) ─────────────────────────────────────────

    /// <summary>
    /// Per-target results as JSON: <c>[{"targetId","targetName","exitCode",
    /// "stdout","stderr","success"}]</c>. Populated after the agents report
    /// back. Fed (with the script) into the next iteration's verdict call.
    /// Stored as jsonb. Empty array until the iteration runs.
    /// </summary>
    public string ResultsJson { get; set; } = "[]";

    // ── Verdict (M11.E.13) ──────────────────────────────────────────────────

    /// <summary>The verdict LLM's classification of this iteration's
    /// results. <see cref="AdhocVerdict.Pending"/> until evaluated.</summary>
    public AdhocVerdict Verdict { get; set; } = AdhocVerdict.Pending;

    /// <summary>Human-readable narrative summarising this iteration's
    /// outcome (M11.E.8 / M11.E.13). Shown on the iteration card.</summary>
    public string Narrative { get; set; } = string.Empty;

    // ── Cost attribution ────────────────────────────────────────────────────

    /// <summary>Provider/model that produced this iteration's script, e.g.
    /// <c>Anthropic/claude-sonnet-4.6</c>.</summary>
    public string LlmModel { get; set; } = string.Empty;

    /// <summary>Prompt tokens consumed across this iteration's LLM calls
    /// (generation + verdict). Counts against the Space's monthly cap.</summary>
    public int LlmPromptTokens { get; set; }

    /// <summary>Completion tokens consumed across this iteration's LLM calls.</summary>
    public int LlmCompletionTokens { get; set; }
}

/// <summary>Lifecycle of an <see cref="AdhocIteration"/>. Stored as int (additive).</summary>
public enum AdhocIterationStatus
{
    /// <summary>Script generated + gate-passed; awaiting operator approval.</summary>
    PendingApproval = 0,

    /// <summary>Operator approved; signed + dispatched (or dispatching).</summary>
    Approved = 1,

    /// <summary>Operator rejected the proposed script (M11.E.4 "Reject").</summary>
    Rejected = 2,

    /// <summary>Dispatched to the frozen target set; agents are running it.</summary>
    Executing = 3,

    /// <summary>All targets reported back; results + verdict recorded.</summary>
    Completed = 4,
}

/// <summary>
/// M11.E.13 — the verdict LLM's classification of an iteration's per-target
/// results. Mirrors the <c>IterationVerdict.Verdict</c> structured-output
/// field. Stored as int (additive). Defined in Core (no AI dependency) so the
/// domain stays free of the <c>KrakenDeploy.Ai</c> chain, mirroring the
/// <see cref="KrakenAiProviderValue"/> convention.
/// </summary>
public enum AdhocVerdict
{
    /// <summary>Not yet evaluated (iteration still pending/executing).</summary>
    Pending = 0,

    /// <summary>Every target reached the desired state; the session can
    /// close.</summary>
    AllSucceeded = 1,

    /// <summary>Some targets failed but the LLM has no safe fix to propose;
    /// the session closes — manual intervention required.</summary>
    NoFixAvailable = 2,

    /// <summary>The LLM proposed a fix script for the next iteration's
    /// approval dialog.</summary>
    ProposeFix = 3,
}
