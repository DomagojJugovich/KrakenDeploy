using System.ComponentModel;

namespace KrakenDeploy.Server.Data.Services.Ai.Adhoc;

/// <summary>
/// M11.E.13 — structured-output shape the verdict LLM fills after an
/// iteration's per-target results have streamed back. Passed as <c>TResult</c>
/// to <c>IKrakenAi.CompleteAsync&lt;IterationVerdict&gt;</c>; the provider's
/// structured-output mode constrains the model to this shape.
/// <para>
/// The model receives <c>{ originalPrompt, mode, priorIteration.script,
/// perTargetResults: [{ target, exitCode, stdout, stderr }] }</c> and decides
/// whether the iteration solved the operator's request, can't be solved, or
/// can be retried with a proposed fix script. The model has NO field through
/// which to influence the session's frozen target set — that's the M11.E.15a
/// invariant; any proposed fix runs on the SAME frozen set as iteration 1.
/// </para>
/// </summary>
public sealed class IterationVerdict
{
    [Description(
        "Verdict classification: exactly one of \"AllSucceeded\" (every target " +
        "reached the desired state; close the session), \"NoFixAvailable\" (some " +
        "targets failed and no safe fix is possible — manual intervention required, " +
        "close the session), or \"ProposeFix\" (a follow-up script is proposed for " +
        "the next iteration's operator approval).")]
    public string Verdict { get; set; } = "NoFixAvailable";

    [Description(
        "Human-readable narrative summarising what happened in this iteration: " +
        "which targets succeeded, which failed and why, and (if ProposeFix) what " +
        "the proposed fix changes. 1–3 sentences. Shown on the iteration card.")]
    public string Narrative { get; set; } = string.Empty;

    [Description(
        "When Verdict=ProposeFix: the PowerShell script body to run on the next " +
        "iteration against the SAME frozen target set. Must be self-contained, " +
        "avoid Invoke-Expression / Add-Type / remoting, and not propose any " +
        "mode-escalation (a readonly session must get a readonly fix). " +
        "Empty / null when Verdict is AllSucceeded or NoFixAvailable.")]
    public string? ProposedScript { get; set; }

    [Description(
        "When Verdict=ProposeFix: one short sentence describing what the proposed " +
        "fix does. Shown verbatim above the code block in the next approval dialog. " +
        "Empty / null otherwise.")]
    public string? ProposedScriptDescription { get; set; }

    [Description(
        "When Verdict=ProposeFix: plain-language risk assessment for the proposed " +
        "fix. Empty / null otherwise.")]
    public string? RiskAssessment { get; set; }
}
