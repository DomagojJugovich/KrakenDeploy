using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Projects;

namespace KrakenDeploy.Server.Core.Domain.Variables;

/// <summary>
/// Holds all variables for a project (one-to-one with <see cref="Project"/>).
/// Created lazily the first time variables are requested for a project.
/// </summary>
public class VariableSet : AuditableEntity, ISpaceScoped
{
    public Guid SpaceId { get; set; }

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public List<Variable> Variables { get; set; } = [];
}
