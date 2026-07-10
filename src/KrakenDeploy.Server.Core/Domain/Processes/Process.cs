using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Processes;

/// <summary>
/// An ordered list of steps owned by either a project (its deployment process) or
/// a runbook — the one <c>processes</c> table replacing the old
/// <c>deployment_processes</c> + <c>runbook_processes</c> pair. The owner is
/// polymorphic: (<see cref="OwnerKind"/>, <see cref="OwnerId"/>) is unique, so
/// each project/runbook has at most one process. There is deliberately NO FK to
/// the owner table (a polymorphic FK can't be modelled); the owning service
/// deletes the process when the owner is deleted.
/// </summary>
public class Process : Entity, ISpaceScoped
{
    /// <summary>Inherited from the owning project/runbook; stamped on insert so
    /// by-id / by-owner reads are Space-safe.</summary>
    public Guid SpaceId { get; set; }

    /// <summary>Whether the owner is a project or a runbook.</summary>
    public ProcessOwnerKind OwnerKind { get; set; }

    /// <summary>The owning project id (<see cref="ProcessOwnerKind.Project"/>) or
    /// runbook id (<see cref="ProcessOwnerKind.Runbook"/>).</summary>
    public Guid OwnerId { get; set; }

    public ICollection<ProcessStep> Steps { get; set; } = [];
}
