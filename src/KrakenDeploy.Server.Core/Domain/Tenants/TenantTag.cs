using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Targets;

namespace KrakenDeploy.Server.Core.Domain.Tenants;

/// <summary>
/// A single tag within a <see cref="TagSet"/> (e.g. "Production" in set "Tier").
/// Tags are applied to deployment targets to indicate which tenants they serve.
/// </summary>
public class TenantTag : AuditableEntity, ISpaceScoped
{
    /// <summary>Inherited from the owning TagSet/Tenant; stamped on insert and
    /// backfilled for existing rows so by-id reads/mutations are Space-safe.</summary>
    public Guid SpaceId { get; set; }

    public Guid TagSetId { get; set; }
    public TagSet TagSet { get; set; } = null!;

    public required string Name { get; set; }

    /// <summary>Optional CSS/hex colour for UI display (e.g. "#e63946").</summary>
    public string? Color { get; set; }

    /// <summary>Targets that carry this tag.</summary>
    public ICollection<DeploymentTarget> Targets { get; set; } = [];
}
