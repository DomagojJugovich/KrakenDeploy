using System.ComponentModel;

namespace KrakenDeploy.Server.Data.Services.Ai.Adhoc;

/// <summary>
/// M11.E.2 — the structured-output shape the LLM fills for an ad-hoc script
/// generation. Passed as <c>TResult</c> to
/// <c>IKrakenAi.CompleteAsync&lt;AdhocGenerationResult&gt;</c>; the provider's
/// structured-output mode (Anthropic tool use / OpenAI json_schema) constrains
/// the model to this exact shape. The <see cref="Description"/> attributes
/// feed the generated JSON schema so the model knows what each field means
/// and the operator-approval dialog has consistent copy to render.
/// </summary>
public sealed class AdhocGenerationResult
{
    [Description(
        "One short sentence describing in plain language what the script does. " +
        "Shown verbatim in the operator-approval dialog above the code block.")]
    public string Description { get; set; } = string.Empty;

    [Description(
        "The PowerShell script body to run on each target in the frozen target set. " +
        "Must be self-contained PowerShell — no remoting (the agent runs ON the target). " +
        "Prefer Get-/Test-/Measure-* cmdlets for readonly sessions; do NOT use " +
        "Invoke-Expression, Add-Type, Invoke-Command -ComputerName, " +
        "Remove-Item -Recurse -Force, registry-write cmdlets, or service install/uninstall.")]
    public string GeneratedScript { get; set; } = string.Empty;

    [Description(
        "Short description of the output the script is expected to produce — what " +
        "the operator should see if it ran cleanly. Helps the operator sanity-check.")]
    public string ExpectedOutputShape { get; set; } = string.Empty;

    [Description(
        "Plain-language risk assessment. For readonly: usually 'None — read-only'. " +
        "For mutating: name the specific state changes (services stopped, files written, " +
        "etc.) and any irreversible operations.")]
    public string RiskAssessment { get; set; } = string.Empty;

    [Description(
        "True if the script changes target state in any way (writes, service control, " +
        "config changes). Must be false for a readonly session — the static-analysis " +
        "gate rejects the script if this disagrees with the session's mode.")]
    public bool RequiresMutation { get; set; }
}
