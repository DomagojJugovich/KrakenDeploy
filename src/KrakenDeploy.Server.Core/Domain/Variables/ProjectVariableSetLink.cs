using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Variables;

/// <summary>
/// Join row connecting a <see cref="Projects.Project"/> to a library
/// <see cref="VariableSet"/> it includes. Ordered by <see cref="SortOrder"/>:
/// higher order overlays later, so a higher-order library set's value wins
/// over a lower-order one for the same variable name. A project's own
/// variables always win over any included library set.
/// <para>
/// Join POCO (composite key <c>(project_id, variable_set_id)</c>, no <c>Id</c>).
/// Space-scoped: it carries a stamped <see cref="SpaceId"/> and composite FKs
/// <c>(space_id, project_id)</c> / <c>(space_id, variable_set_id)</c> that pin
/// both ends to the same Space, so a project can never include a library set
/// from another Space.
/// </para>
/// </summary>
public class ProjectVariableSetLink : ISpaceScoped
{
    public Guid SpaceId { get; set; }

    public Guid ProjectId { get; set; }

    public Guid VariableSetId { get; set; }

    /// <summary>Overlay order among a project's included library sets (ascending).</summary>
    public int SortOrder { get; set; }
}
