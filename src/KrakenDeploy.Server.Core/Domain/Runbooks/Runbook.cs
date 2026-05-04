using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Projects;

namespace KrakenDeploy.Server.Core.Domain.Runbooks;

/// <summary>
/// A runbook is a named automation sequence scoped to a project that can be triggered
/// against any environment without creating a release. The runbook owns a
/// <see cref="RunbookProcess"/> (its editable steps), and each execution is recorded
/// as a <see cref="RunbookRun"/> that snaps the current process at trigger time.
/// </summary>
public class Runbook : AuditableEntity
{
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>Editable step list — snapped into each run at dispatch time.</summary>
    public RunbookProcess? Process { get; set; }
}
