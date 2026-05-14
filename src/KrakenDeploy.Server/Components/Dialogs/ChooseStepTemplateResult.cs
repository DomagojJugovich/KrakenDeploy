using KrakenDeploy.Server.Core.Domain.StepTemplates;

namespace KrakenDeploy.Server.Components.Dialogs;

/// <summary>
/// Outcome of <c>ChooseStepTemplateDialog</c>. The caller branches on which
/// field is populated:
/// <list type="bullet">
///   <item><see cref="IsBlankScriptStep"/> = user picked the "Run a Script"
///         sentinel; open the existing script-step form.</item>
///   <item><see cref="Template"/> is non-null = user picked an installed
///         template (which may have been just-now installed from the
///         community catalog); use its <c>ActionType</c> and seed
///         <c>DeploymentStep.Config</c> from the template's <c>Properties</c>.</item>
/// </list>
/// A null return value from the dialog means the user cancelled.
/// </summary>
public sealed record ChooseStepTemplateResult(bool IsBlankScriptStep, StepTemplate? Template)
{
    public static ChooseStepTemplateResult Blank { get; } = new(true, null);

    public static ChooseStepTemplateResult FromTemplate(StepTemplate template) =>
        new(false, template);
}
