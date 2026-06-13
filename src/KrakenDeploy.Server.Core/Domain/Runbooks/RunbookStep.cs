using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Processes;

namespace KrakenDeploy.Server.Core.Domain.Runbooks;

/// <summary>
/// A single step within a <see cref="RunbookProcess"/>. Mirrors
/// <c>DeploymentStep</c> but is FK'd to a runbook process instead of a
/// deployment process, keeping runbook steps independently versioned.
/// </summary>
public class RunbookStep : Entity, IComposableStep, ISpaceScoped
{
    /// <summary>Inherited from the owning process/runbook; stamped on insert and
    /// backfilled for existing rows so by-stepId reads/mutations are Space-safe.
    /// Platform-wide scans (step-package usage) must use IgnoreQueryFilters.</summary>
    public Guid SpaceId { get; set; }

    public Guid ProcessId { get; set; }
    public RunbookProcess Process { get; set; } = null!;

    public required string Name { get; set; }
    public required string StepType { get; set; }
    public int SortOrder { get; set; }

    /// <summary>Roles filter — empty means all targets in the run.</summary>
    public List<string> TargetRoles { get; set; } = [];

    /// <summary>Optional package ID (runbooks may not deploy packages).</summary>
    public string PackageId { get; set; } = "";

    /// <summary>Step-type-specific configuration stored as jsonb.</summary>
    public Dictionary<string, string> Config { get; set; } = [];

    /// <summary>
    /// Phase D-6: pinned step-package name. See
    /// <see cref="Processes.DeploymentStep.StepPackageName"/>.
    /// </summary>
    public string? StepPackageName { get; set; }

    /// <summary>
    /// Phase D-6: pinned step-package version. See
    /// <see cref="Processes.DeploymentStep.StepPackageVersion"/>.
    /// </summary>
    public string? StepPackageVersion { get; set; }

    // ── M15 step composition (child steps + ForEach) ──────────────────

    /// <summary>
    /// M15 — when set, marks this step as a child of another step in
    /// the same <see cref="RunbookProcess"/>. Only steps of type
    /// <see cref="KrakenStepTypes.StepGroup"/> may have children;
    /// validation in <c>RunbookService.ValidateAsync</c> enforces this.
    ///
    /// <para>
    /// Mirrors <see cref="Processes.DeploymentStep.ParentStepId"/>. The
    /// runbook run worker pre-flattens the tree at dispatch time via
    /// <c>DeploymentPlanFlattener</c> so the agent receives a flat plan
    /// just as today; ForEach iteration + cross-step output references
    /// work via the same machinery as deployment processes.
    /// </para>
    /// </summary>
    public Guid? ParentStepId { get; set; }

    /// <summary>Navigation to the parent step (M15). Null for top-level steps.</summary>
    public RunbookStep? Parent { get; set; }

    /// <summary>Navigation to child steps (M15). Empty for leaf-type steps.</summary>
    public ICollection<RunbookStep> Children { get; set; } = [];
}
