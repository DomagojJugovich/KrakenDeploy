using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Projects;

/// <summary>
/// Explicit join row for the <see cref="Project"/> ↔ <see cref="Tenants.Tenant"/>
/// many-to-many (the tenants connected to a project). Replaces the former implicit
/// EF join (auto-columns <c>projects_id</c> / <c>tenants_id</c>, no <c>space_id</c>).
/// <para>
/// Space-scoped: the composite FKs
/// <c>(space_id, project_id) → projects(space_id, id)</c> and
/// <c>(space_id, tenant_id) → tenants(space_id, id)</c> pin both ends to the same
/// Space. <c>space_id</c> is stamped on insert by <c>SpaceScopingInterceptor</c>.
/// </para>
/// </summary>
public class ProjectTenant : ISpaceScoped
{
    public Guid SpaceId { get; set; }

    public Guid ProjectId { get; set; }

    public Guid TenantId { get; set; }
}
