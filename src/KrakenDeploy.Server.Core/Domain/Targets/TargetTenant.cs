using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Targets;

/// <summary>
/// Explicit join row for the <see cref="DeploymentTarget"/> ↔
/// <see cref="Tenants.Tenant"/> many-to-many ("Associated Tenants"). Replaces the
/// former implicit EF join (auto-columns <c>deployment_target_id</c> /
/// <c>tenants_id</c>, no <c>space_id</c>).
/// <para>
/// Space-scoped: both ends live in the same Space. The composite FKs
/// <c>(space_id, target_id) → deployment_targets(space_id, id)</c> and
/// <c>(space_id, tenant_id) → tenants(space_id, id)</c> enforce that at the DB
/// level, so a target in one Space can never be associated with a tenant in
/// another. <c>space_id</c> is stamped on insert by <c>SpaceScopingInterceptor</c>.
/// </para>
/// </summary>
public class TargetTenant : ISpaceScoped
{
    public Guid SpaceId { get; set; }

    public Guid TargetId { get; set; }

    public Guid TenantId { get; set; }
}
