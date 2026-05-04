using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Lifecycles;
using KrakenDeploy.Server.Core.Domain.Projects;

namespace KrakenDeploy.Server.Core.Domain.Channels;

/// <summary>
/// A release channel groups releases with a shared lifecycle and optional version rules.
/// Every project has at least one channel (the default). Releases created without an
/// explicit channel are assigned to the project's default channel.
/// </summary>
public class Channel : AuditableEntity
{
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
    /// SemVer range string (e.g. <c>"&gt;=1.0.0 &lt;2.0.0"</c>).
    /// When set, only release versions matching this range may be created on this channel.
    /// </summary>
    public string? VersionRange { get; set; }

    /// <summary>
    /// Pre-release tag filter (e.g. <c>"beta"</c>).
    /// When set, only versions whose pre-release label equals this value are accepted.
    /// </summary>
    public string? VersionTag { get; set; }
}
