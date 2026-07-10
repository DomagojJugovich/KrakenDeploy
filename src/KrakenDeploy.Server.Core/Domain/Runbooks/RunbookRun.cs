using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Releases;

namespace KrakenDeploy.Server.Core.Domain.Runbooks;

/// <summary>
/// One execution of a <see cref="Runbook"/> — a <see cref="ServerTask"/> of kind
/// <see cref="ServerTaskKind.RunbookRun"/>. The runbook's process is snapped into
/// <see cref="ProcessSnapshot"/> at dispatch time so historical runs stay accurate
/// after the runbook's steps are edited.
/// <para>
/// Since the 2026-07 unification a runbook run shares the deployment orchestrator:
/// it fans out over its <see cref="ServerTask.Targets"/> assignment set (the old
/// single <c>TargetId</c> column is gone) and gains artifacts, output variables,
/// step outcomes, failure mode and scheduling for free.
/// </para>
/// </summary>
public sealed class RunbookRun : ServerTask
{
    public RunbookRun() => Kind = ServerTaskKind.RunbookRun;

    public Guid RunbookId { get; set; }
    public Runbook Runbook { get; set; } = null!;

    /// <summary>Snapshot of the runbook process taken at trigger time.</summary>
    public List<StepSnapshot> ProcessSnapshot { get; set; } = [];
}
