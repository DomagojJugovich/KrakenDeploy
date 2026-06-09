namespace KrakenDeploy.Server.Core.Domain.Variables;

/// <summary>
/// Join row connecting a <see cref="Projects.Project"/> to a library
/// <see cref="VariableSet"/> it includes. Ordered by <see cref="SortOrder"/>:
/// higher order overlays later, so a higher-order library set's value wins
/// over a lower-order one for the same variable name. A project's own
/// variables always win over any included library set.
/// <para>
/// Plain join POCO (composite key, no <c>Id</c>) — not <c>ISpaceScoped</c>;
/// both FK ends are space-scoped, so a row is reachable only within its
/// project's / set's Space.
/// </para>
/// </summary>
public class ProjectVariableSetLink
{
    public Guid ProjectId { get; set; }

    public Guid VariableSetId { get; set; }

    /// <summary>Overlay order among a project's included library sets (ascending).</summary>
    public int SortOrder { get; set; }
}
