using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Ai;

/// <summary>
/// M11.C — the AI's autonomous diagnosis of a failed deployment. One row per
/// deployment (unique on <see cref="DeploymentId"/>); written by the
/// <c>DeploymentDiagnosisWorker</c> after the orchestrator marks a started
/// deployment <c>Failed</c>. Surfaced as the "AI Analysis" card on the
/// deployment-failure detail page.
/// <para>
/// Space scope inherits through <see cref="DeploymentId"/> — the Deployment
/// row carries the SpaceId — but the aggregate also implements
/// <see cref="ISpaceScoped"/> so the global query filter + the
/// <c>SpaceScopingInterceptor</c> stamp it consistently with the rest of
/// the per-Space data.
/// </para>
/// </summary>
public class DeploymentDiagnosis : AuditableEntity, ISpaceScoped
{
    public Guid SpaceId { get; set; }

    /// <summary>The failed deployment this diagnosis is for. Unique —
    /// re-diagnosis upserts the existing row.</summary>
    public Guid DeploymentId { get; set; }

    /// <summary>One- or two-sentence plain-language probable cause.</summary>
    public string ProbableCause { get; set; } = string.Empty;

    /// <summary>How confident the model is. Low cases get a "verify
    /// yourself" footer in the UI.</summary>
    public DiagnosisConfidence Confidence { get; set; } = DiagnosisConfidence.Low;

    /// <summary>Concrete suggested next step(s) for the operator.</summary>
    public string SuggestedFix { get; set; } = string.Empty;

    /// <summary>
    /// JSON array of the log lines the model flagged as relevant:
    /// <c>[{"sequence":42,"text":"…"}]</c>. The UI turns these into
    /// "show in log" links. Stored as a string (jsonb) rather than a
    /// typed collection because it's display-only — never queried.
    /// </summary>
    public string RelevantLogLinesJson { get; set; } = "[]";

    // Model + token attribution lives in AiCallLog (the single source of truth for
    // AI spend); the never-read model_used/prompt_tokens/completion_tokens columns
    // were dropped in the 2026-07 schema cleanup.
}

/// <summary>Model-reported confidence in a <see cref="DeploymentDiagnosis"/>.
/// Stored as int so adding a variant is additive.</summary>
public enum DiagnosisConfidence
{
    Low = 0,
    Medium = 1,
    High = 2,
}
