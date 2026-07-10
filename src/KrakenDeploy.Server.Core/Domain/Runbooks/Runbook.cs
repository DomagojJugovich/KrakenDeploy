using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Projects;

namespace KrakenDeploy.Server.Core.Domain.Runbooks;

/// <summary>
/// A runbook is a named automation sequence scoped to a project that can be triggered
/// against any environment without creating a release. The runbook owns a
/// <see cref="Processes.Process"/> (its editable steps, keyed by owner), and each
/// execution is recorded as a <see cref="RunbookRun"/> that snaps the current
/// process at trigger time.
/// <para>
/// The process is polymorphic (no owner FK), so there is no navigation property —
/// resolve it via <c>ProcessService</c> / <c>RunbookService</c> by
/// (<c>ProcessOwnerKind.Runbook</c>, runbook id).
/// </para>
/// </summary>
public class Runbook : AuditableEntity, ISpaceScoped
{
    public Guid SpaceId { get; set; }

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public required string Name { get; set; }

    public string? Description { get; set; }
}
