using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Processes;

/// <summary>
/// A single step in a <see cref="DeploymentProcess"/>.
/// The Kraken-native script step type is <c>"Kraken.Script"</c>; imported
/// Octopus processes use <c>"Octopus.Script"</c>. Both share the same handler.
/// <para>
/// Step-type-specific configuration is stored in <see cref="Config"/> as a
/// string-keyed dictionary (persisted as <c>jsonb</c>).
/// For <c>Kraken.Script</c> / <c>Octopus.Script</c> the recognised keys are:
/// <list type="bullet">
///   <item><c>Octopus.Action.Script.ScriptBody</c> — script text.</item>
///   <item><c>Octopus.Action.Script.Syntax</c> — <c>PowerShell</c>, <c>Bash</c>,
///         <c>CSharp</c>, <c>FSharp</c>, or <c>Python</c>.</item>
///   <item><c>Octopus.Action.PowerShell.Edition</c> — <c>Desktop</c> or
///         <c>Core</c> (PowerShell only).</item>
/// </list>
/// </para>
/// </summary>
public class DeploymentStep : Entity
{
    public Guid ProcessId { get; set; }
    public DeploymentProcess Process { get; set; } = null!;

    public required string Name { get; set; }

    /// <summary>Step type identifier, e.g. "Kraken.Script".</summary>
    public required string StepType { get; set; }

    /// <summary>Zero-based execution order within the process.</summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// Only targets whose roles overlap this list will execute this step.
    /// An empty list means the step runs on all targets in the deployment.
    /// </summary>
    public List<string> TargetRoles { get; set; } = [];

    /// <summary>
    /// The logical package ID (e.g. "MyApp") that this step deploys.
    /// The specific version is chosen at release creation time.
    /// </summary>
    public required string PackageId { get; set; }

    /// <summary>Step-type-specific configuration stored as jsonb.</summary>
    public Dictionary<string, string> Config { get; set; } = [];

    /// <summary>
    /// The pinned <see cref="StepPackages.StepPackage.Name"/> that supplies
    /// the handler for this step (Phase D-6). Step type and package name
    /// differ (e.g. step type <c>Octopus.IIS</c> lives in package
    /// <c>octopus.iis</c>), so the pin must carry both. <c>null</c> when
    /// no installed package claims the step type — the agent then falls
    /// back to its hardcoded handler.
    /// </summary>
    public string? StepPackageName { get; set; }

    /// <summary>
    /// The pinned <see cref="StepPackages.StepPackage.Version"/> the agent
    /// loads alongside <see cref="StepPackageName"/> (Phase D-6). Must be
    /// non-null when <see cref="StepPackageName"/> is non-null (the agent
    /// trusts the pair as a unit).
    /// <para>
    /// Pin is exact: the editor (D-7) writes whichever version the user
    /// picked, and release creation (<see cref="Releases.StepSnapshot"/>)
    /// freezes the exact pair so the release stays reproducible.
    /// </para>
    /// </summary>
    public string? StepPackageVersion { get; set; }
}
