using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.StepTemplates;

/// <summary>
/// A discovered step-template in the OctopusDeploy/Library GitHub repo. Cached
/// server-side by <c>StepTemplateCatalogService</c>'s hourly poll so the
/// /step-templates/community browser doesn't have to hit GitHub on every page
/// load. Installing one materialises a real <see cref="StepTemplate"/> via
/// <see cref="StepTemplates.StepTemplate.Source"/> = <c>CommunityLibrary</c>.
/// </summary>
public class StepTemplateCatalogEntry : Entity
{
    /// <summary>
    /// Octopus's <c>CommunityActionTemplateId</c> / <c>Id</c> field — the
    /// stable identifier used to upsert. Required.
    /// </summary>
    public required string CommunityTemplateId { get; set; }

    /// <summary>
    /// SC6: which feed the entry came from, as lower-cased <c>owner/repo</c>
    /// (e.g. <c>octopusdeploy/library</c>). The catalog is multi-feed now;
    /// orphan removal and SHA-diffing are scoped per feed — without this,
    /// syncing feed A would delete every row feed B produced.
    /// </summary>
    public string FeedKey { get; set; } = "";

    /// <summary>Path relative to the repo root (e.g. <c>step-templates/aws-tag-resources.json</c>).</summary>
    public required string PathInRepo { get; set; }

    /// <summary>Git blob SHA of the JSON file at the time of last fetch.</summary>
    public required string FileSha { get; set; }

    /// <summary>Direct download URL on <c>raw.githubusercontent.com</c>.</summary>
    public required string DownloadUrl { get; set; }

    /// <summary>Display name (from the JSON's <c>Name</c> field).</summary>
    public required string Name { get; set; }

    /// <summary>Step type identifier (e.g. <c>Octopus.AwsRunCloudFormation</c>).</summary>
    public required string ActionType { get; set; }

    public string? Description { get; set; }
    public string? Category    { get; set; }
    public string? Author      { get; set; }
    public string? Website     { get; set; }
    public string? LogoUrl     { get; set; }

    /// <summary>Version number from the JSON.</summary>
    public int Version { get; set; }

    /// <summary>When the entry was last synced from GitHub.</summary>
    public DateTimeOffset LastSyncedUtc { get; set; }
}
