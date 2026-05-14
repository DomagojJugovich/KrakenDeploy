using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.StepTemplates;

/// <summary>
/// A reusable step template — compatible with the Octopus Community Library JSON format.
/// Templates can be authored directly in KrakenDeploy or imported from the Octopus Library.
/// When applied to a deployment process step, the template's <see cref="ActionType"/>
/// and <see cref="Properties"/> are copied onto the <c>DeploymentStep</c>.
/// </summary>
public class StepTemplate : AuditableEntity, ISpaceScoped
{
    public Guid SpaceId { get; set; }

    /// <summary>Display name shown in the Library and step picker.</summary>
    public required string Name { get; set; }

    /// <summary>Optional markdown-compatible description shown in the UI.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Step type identifier, e.g. <c>Octopus.Script</c> or <c>Kraken.Script</c>.
    /// Stored verbatim on <c>DeploymentStep.StepType</c> when the template is applied.
    /// </summary>
    public required string ActionType { get; set; }

    /// <summary>
    /// Monotonically increasing version number. Incremented on every save so that
    /// existing process steps can detect when the source template was updated.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// Action properties from the Octopus Library JSON
    /// (e.g. <c>Octopus.Action.Script.ScriptBody</c>, <c>Octopus.Action.Script.Syntax</c>).
    /// Also stores KrakenDeploy-native keys such as <c>scriptBody</c> and <c>scriptSyntax</c>.
    /// Persisted as <c>jsonb</c>.
    /// </summary>
    public Dictionary<string, string> Properties { get; set; } = [];

    /// <summary>
    /// Input parameters the user fills in when adding this template to a process step.
    /// Persisted as a <c>jsonb</c> array.
    /// </summary>
    public List<StepTemplateParameter> Parameters { get; set; } = [];

    /// <summary>
    /// The <c>CommunityActionTemplateId</c> from the Octopus Library JSON, if imported.
    /// Allows matching a local template back to its upstream source for update checks.
    /// </summary>
    public string? CommunityTemplateId { get; set; }

    /// <summary>
    /// Human-readable import source — a URL or filename — set when the template
    /// was imported rather than authored in KrakenDeploy directly.
    /// </summary>
    public string? ImportedFrom { get; set; }

    /// <summary>
    /// Origin of this template. Drives badge / filter UI and whether the row
    /// is auto-managed (Built-in reseeds on startup) vs user-owned.
    /// </summary>
    public StepTemplateSource Source { get; set; } = StepTemplateSource.UserAuthored;

    /// <summary>
    /// Small-bucket category from the Octopus Library JSON (e.g. <c>"aws"</c>,
    /// <c>"iis"</c>, <c>"windows-iis"</c>). UI displays grouped by the
    /// big-bucket category derived via <c>StepTemplateCategoryMap</c>.
    /// </summary>
    public string? Category { get; set; }

    /// <summary>Author name as it appears in the Octopus Library JSON.</summary>
    public string? Author { get; set; }

    /// <summary>Author or template homepage URL.</summary>
    public string? Website { get; set; }

    /// <summary>URL of an icon to show next to the template in the picker.</summary>
    public string? LogoUrl { get; set; }
}
