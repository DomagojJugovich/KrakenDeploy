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
}
