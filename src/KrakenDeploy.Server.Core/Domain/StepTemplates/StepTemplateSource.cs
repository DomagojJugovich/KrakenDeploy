namespace KrakenDeploy.Server.Core.Domain.StepTemplates;

/// <summary>
/// Where a <see cref="StepTemplate"/> came from. Drives badge / filter UI
/// (Featured vs Community vs User-authored) and informs whether the row
/// is auto-managed (Built-in is reseeded on startup) or user-owned.
/// </summary>
public enum StepTemplateSource
{
    /// <summary>Author manually wrote it in the UI or via API.</summary>
    UserAuthored = 0,

    /// <summary>Seeded by <c>BuiltInStepTemplateSeeder</c> at startup.</summary>
    BuiltIn = 1,

    /// <summary>
    /// Imported from the Octopus Deploy Community Library at
    /// <c>github.com/OctopusDeploy/Library/tree/master/step-templates</c>.
    /// </summary>
    CommunityLibrary = 2,

    /// <summary>
    /// Imported from a local file (single-template paste-JSON or the bulk
    /// "Import from folder" feature). Not auto-refreshed.
    /// </summary>
    LocalImport = 3,
}
