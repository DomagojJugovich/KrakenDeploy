namespace KrakenDeploy.Server.Core.Domain.Tags;

/// <summary>
/// The entity kinds a <see cref="TagSet"/> can be scoped to and a
/// <see cref="TagApplication"/> can point at. Mirrors Octopus extended tag
/// sets (Tenant / Project / Environment / Runbook) plus deployment targets.
/// <para>
/// Values are stable storage contracts (persisted in <c>tag_sets.scopes</c>
/// and <c>tag_applications.entity_kind</c>) — append only, never renumber.
/// </para>
/// </summary>
public enum TaggableEntityKind
{
    Tenant           = 0,
    Project          = 1,
    Environment      = 2,
    Runbook          = 3,
    DeploymentTarget = 4,
}
