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

    /// <summary>
    /// Per-runbook override for how many successful runs are kept per
    /// (runbook, environment) by retention (WP9). <c>null</c> (the default)
    /// inherits the instance-wide <c>PerformanceSettings.RunbookRunRetentionKeep</c>;
    /// a value overrides it for this runbook only. <c>0</c> disables run pruning
    /// for this runbook (keep all). Wired into
    /// <c>RetentionService.PruneAfterRunbookRunAsync</c>'s reserved
    /// <c>keepOverride</c> hook.
    /// </summary>
    public int? RetentionKeepRuns { get; set; }

    /// <summary>
    /// F6 — author consent that this runbook's process is self-contained and may
    /// run alongside other CONSENTING work on a shared machine. The runbook
    /// analogue of <c>Project.AllowParallelTaskExecution</c> (a deliberately
    /// SEPARATE flag — locked decision 2026-07-25: one project knob must not
    /// silently cover its runbooks). OR-composed with the target's own flag into
    /// the run's per-target mode at claim time and into
    /// <c>DeploymentPlan.AllowParallelTaskExecution</c> at plan build. Default
    /// <c>false</c>: runs hold each target exclusively for the whole plan.
    /// </summary>
    public bool AllowParallelTaskExecution { get; set; }
}
