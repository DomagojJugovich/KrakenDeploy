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

    /// <summary>
    /// Snapshot of the project's <see cref="Variables.Variable"/> rows at
    /// release-creation OR last "Update Variables" time. Tenant common
    /// variables are NOT frozen here — they're resolved live at deployment
    /// time (their semantics match per-tenant instance config, which should
    /// not be release-pinned).
    /// <para>
    /// Defaults to an empty list. Use <see cref="VariableSnapshotUpdatedUtc"/>
    /// to distinguish "explicitly snapshotted as empty" (timestamp set) from
    /// "predates the snapshot feature" (timestamp <c>null</c>). The latter
    /// makes the deployment worker fall back to live project-variable
    /// resolution with a warning, so historical releases stay deployable
    /// through the migration window.
    /// </para>
    /// </summary>
    public List<VariableSnapshot> VariableSnapshot { get; set; } = [];

    /// <summary>
    /// When the variable snapshot was last refreshed — set at release
    /// creation and bumped each time <c>ReleaseService.UpdateVariablesAsync</c>
    /// runs. <c>null</c> means the release predates the snapshot feature
    /// AND the <see cref="VariableSnapshot"/> is meaningless (don't read it).
    /// Surfaced in the per-release UI as the "Variables snapshotted at" timestamp.
    /// </summary>
    public DateTimeOffset? VariableSnapshotUpdatedUtc { get; set; }

    public Guid? ChannelId { get; set; }
    public Channel? Channel { get; set; }
}
