namespace KrakenDeploy.Server.Core.Domain.StepTemplates;

/// <summary>
/// An input parameter definition for a <see cref="StepTemplate"/>.
/// Stored as a JSON array inside the <c>step_templates</c> row.
/// Drives the UI form when a user adds the template to a deployment process step.
/// </summary>
public sealed class StepTemplateParameter
{
    /// <summary>
    /// The variable name used inside the script body, e.g. <c>ServerName</c>.
    /// Matches the <c>Name</c> field in the Octopus Library JSON.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>Human-readable label shown in the UI form.</summary>
    public required string Label { get; init; }

    /// <summary>Tooltip / help text shown alongside the form control.</summary>
    public string? HelpText { get; init; }

    /// <summary>Pre-filled default value for the parameter.</summary>
    public string? DefaultValue { get; init; }

    /// <summary>
    /// Radzen form control type mapped from <c>Octopus.ControlType</c>.
    /// Supported values: <c>SingleLineText</c>, <c>MultiLineText</c>, <c>Select</c>,
    /// <c>Checkbox</c>, <c>Sensitive</c>, <c>Package</c>.
    /// Defaults to <c>SingleLineText</c>.
    /// </summary>
    public string ControlType { get; init; } = "SingleLineText";

    /// <summary>Non-empty only for <c>Select</c> controls: the list of allowed values.</summary>
    public List<string> SelectOptions { get; init; } = [];
}
