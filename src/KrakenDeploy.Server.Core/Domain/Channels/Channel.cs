using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Lifecycles;
using KrakenDeploy.Server.Core.Domain.Projects;

namespace KrakenDeploy.Server.Core.Domain.Channels;

/// <summary>
/// A release channel groups releases with a shared lifecycle and optional version rules.
/// Every project has at least one channel (the default). Releases created without an
/// explicit channel are assigned to the project's default channel.
/// </summary>
public class Channel : AuditableEntity, ISpaceScoped
{
    public Guid SpaceId { get; set; }

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public required string Name { get; set; }

    /// <summary>Exactly one channel per project carries this flag.</summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// Lifecycle that governs deployments of releases on this channel.
    /// <c>null</c> means no lifecycle gates are enforced.
    /// </summary>
    public Guid? LifecycleId { get; set; }
    public Lifecycle? Lifecycle { get; set; }

    /// <summary>
    /// NuGet-style version range (Octopus semantics), e.g. <c>"[1.0,2.0)"</c>,
    /// <c>"1.2.*"</c>, or <c>"[1.0.0,)"</c>. When set, every package version pinned
    /// into a release on this channel must satisfy the range
    /// (<c>NuGet.Versioning.VersionRange.Satisfies</c>). Validated at channel save
    /// and enforced at release creation.
    /// </summary>
    public string? VersionRange { get; set; }

    /// <summary>
    /// Pre-release tag filter as a regular expression (Octopus semantics), matched
    /// against each package version's pre-release label — e.g. <c>"^$"</c> for
    /// stable-only, <c>"^beta"</c> for beta builds. When set, every pinned package
    /// version's pre-release tag must match. Validated at channel save and enforced
    /// at release creation.
    /// </summary>
    public string? VersionTag { get; set; }
}
