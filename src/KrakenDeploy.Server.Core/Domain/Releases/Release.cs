using KrakenDeploy.Server.Core.Domain.Channels;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Projects;

namespace KrakenDeploy.Server.Core.Domain.Releases;

/// <summary>
/// A versioned snapshot of a project's deployment process with pinned package versions.
/// Creating a release locks in the process steps at that point in time so that
/// historical deployments remain reproducible.
/// </summary>
public class Release : AuditableEntity, ISpaceScoped
{
    public Guid SpaceId { get; set; }

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public required string Version { get; set; }
    public string? ReleaseNotes { get; set; }

    /// <summary>Snapshot of the deployment process steps taken at release creation time.</summary>
    public List<StepSnapshot> ProcessSnapshot { get; set; } = [];

    public Guid? ChannelId { get; set; }
    public Channel? Channel { get; set; }
}
