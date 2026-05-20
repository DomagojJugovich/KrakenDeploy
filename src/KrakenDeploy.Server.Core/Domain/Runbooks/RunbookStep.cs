using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Runbooks;

/// <summary>
/// A single step within a <see cref="RunbookProcess"/>. Mirrors
/// <c>DeploymentStep</c> but is FK'd to a runbook process instead of a
/// deployment process, keeping runbook steps independently versioned.
/// </summary>
public class RunbookStep : Entity
{
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
}
