using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Targets;

/// <summary>
/// Explicit join row for the <see cref="DeploymentTarget"/> ↔
/// <see cref="Environments.DeploymentEnvironment"/> many-to-many (the environments
/// a target serves). Replaces the former implicit EF join (auto-columns
/// <c>deployment_target_id</c> / <c>environments_id</c>, no <c>space_id</c>).
/// <para>
/// Space-scoped: the composite FKs
/// <c>(space_id, target_id) → deployment_targets(space_id, id)</c> and
/// <c>(space_id, environment_id) → environments(space_id, id)</c> pin both ends to
/// the same Space. <c>space_id</c> is stamped on insert by
/// <c>SpaceScopingInterceptor</c>.
/// </para>
/// </summary>
public class TargetEnvironment : ISpaceScoped
{
    public Guid SpaceId { get; set; }

    public Guid TargetId { get; set; }

    public Guid EnvironmentId { get; set; }
}
