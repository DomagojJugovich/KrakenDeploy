using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Projects;

namespace KrakenDeploy.Server.Core.Domain.Variables;

/// <summary>
/// A named bag of <see cref="Variable"/>s. Plays one of three roles
/// (see <see cref="VariableSetKind"/>):
/// <list type="bullet">
///   <item><b>Project</b> — one-to-one with a <see cref="Project"/>, created
///         lazily the first time variables are requested. Has a non-null
///         <see cref="ProjectId"/>, no <see cref="Name"/>.</item>
///   <item><b>Library</b> — a reusable set projects include. Has a
///         <see cref="Name"/>, null <see cref="ProjectId"/>.</item>
///   <item><b>TenantCommon</b> — referenced by a tenant (reserved).</item>
/// </list>
/// </summary>
public class VariableSet : AuditableEntity, ISpaceScoped
{
    public Guid SpaceId { get; set; }

    /// <summary>
    /// Owning project for <see cref="VariableSetKind.Project"/> sets; null for
    /// library / tenant-common sets. The DB enforces uniqueness only over
    /// non-null values (filtered unique index).
    /// </summary>
    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }

    /// <summary>Discriminates project / library / tenant-common sets.</summary>
    public VariableSetKind Kind { get; set; } = VariableSetKind.Project;

    /// <summary>Display name — required for <see cref="VariableSetKind.Library"/>; null for project sets.</summary>
    public string? Name { get; set; }

    /// <summary>Optional human description (library sets).</summary>
    public string? Description { get; set; }

    public List<Variable> Variables { get; set; } = [];
}
