using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Tenants;

/// <summary>
/// A named group of tags belonging to a tenant (e.g. "Region", "Tier").
/// Tags within a set are mutually exclusive per target by convention.
/// </summary>
public class TagSet : AuditableEntity
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>Display ordering within the tenant (ascending).</summary>
    public int SortOrder { get; set; }

    /// <summary>Tags belonging to this set.</summary>
    public ICollection<TenantTag> Tags { get; set; } = [];
}
