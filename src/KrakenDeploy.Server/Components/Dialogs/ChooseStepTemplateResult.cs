using KrakenDeploy.Server.Core.Domain.StepTemplates;

namespace KrakenDeploy.Server.Components.Dialogs;

/// <summary>
/// Outcome of <c>ChooseStepTemplateDialog</c>. The caller branches on which
/// field is populated:
/// <list type="bullet">
///   <item><see cref="StepTypeId"/> is non-null = user picked an installed
///         step TYPE card (SC5 — sourced from the step-type registry); open
///         the form with <c>ActionType = StepTypeId</c>.</item>
///   <item><see cref="IsStepGroup"/> = user picked the "Step Group" card
///         (M15). Opens the form with <c>ActionType = "Kraken.StepGroup"</c>
///         which renders the group-specific editor body (Target Roles +
///         optional ForEach panel).</item>
///   <item><see cref="Template"/> is non-null = user picked a PRESET (a
///         community/user step template, possibly just-now installed from
///         the community catalog); use its <c>ActionType</c> and seed
///         <c>DeploymentStep.Config</c> from the template's <c>Properties</c>.</item>
///   <item><see cref="IsBlankScriptStep"/> = legacy "Run a Script" sentinel;
///         kept for compatibility — the SC5 picker emits the script type as
///         a <see cref="StepTypeId"/> card instead.</item>
/// </list>
/// A null return value from the dialog means the user cancelled.
/// </summary>
public sealed record ChooseStepTemplateResult(
    bool IsBlankScriptStep,
    bool IsStepGroup,
    StepTemplate? Template,
    string? StepTypeId = null,
    string? StepTypeDisplayName = null)
{
    public static ChooseStepTemplateResult Blank { get; } = new(true, false, null);

    public static ChooseStepTemplateResult Group { get; } = new(false, true, null);

    public static ChooseStepTemplateResult FromTemplate(StepTemplate template) =>
        new(false, false, template);

    public static ChooseStepTemplateResult FromStepType(string typeId, string displayName) =>
        new(false, false, null, typeId, displayName);
}
