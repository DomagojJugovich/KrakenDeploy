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

    // ── M14 step-execution knobs ─────────────────────────────────────────
    // M14.1 lands the schema + storage + importer + UI fields. The
    // orchestrator (DeploymentWorker) starts consulting the fields in
    // later phases (M14.2 = Condition + Required + Timeout, M14.3 =
    // Retries, M14.4 = StartTrigger / parallel). Defaults preserve
    // today's pre-M14 behaviour: Success Condition, Required=true,
    // no retries, no timeout, sequential.

    /// <summary>
    /// M14.2 Run Condition: when should this step run based on prior
    /// outcomes? Default <see cref="StepCondition.Success"/> matches
    /// pre-M14 behaviour (orchestrator stopped on first failure).
    /// </summary>
    public StepCondition Condition { get; set; } = StepCondition.Success;

    /// <summary>
    /// M14.2 Variable-condition expression — Octostache template
    /// evaluated when <see cref="Condition"/> is
    /// <see cref="StepCondition.Variable"/>. Truthy ("true" or "1"
    /// case-insensitive) → run; falsy or unresolved → skip.
    /// </summary>
    public string? ConditionVariableExpression { get; set; }

    /// <summary>
    /// M14.2 Required: when <c>true</c>, a step failure aborts the
    /// deployment. When <c>false</c>, a failure marks the deployment
    /// as having failures but the loop continues — subsequent
    /// <see cref="StepCondition.Failure"/> / <see cref="StepCondition.Always"/>
    /// steps still run.
    /// <para>
    /// KrakenDeploy default is <c>true</c> (preserves pre-M14 behaviour
    /// where any step failure aborted). Note: Octopus defaults action
    /// <c>IsRequired</c> to <c>false</c>; the importer preserves the
    /// source value, so an imported Octopus process keeps its semantics.
    /// </para>
    /// </summary>
    public bool Required { get; set; } = true;

    /// <summary>
    /// M14.3 Retry attempt count. Number of additional attempts after
    /// the first failure. <c>0</c> (default) disables retries.
    /// </summary>
    public int MaxRetries { get; set; }

    /// <summary>
    /// M14.3 Delay between retry attempts, in seconds. Honoured only
    /// when <see cref="MaxRetries"/> &gt; 0.
    /// </summary>
    public int RetryDelaySeconds { get; set; }

    /// <summary>
    /// M14.2 Per-step timeout in seconds. <c>0</c> (default) = unlimited.
    /// Applied as a <c>CancellationTokenSource.CancelAfter</c> wrapping
    /// the step execution. Server-side steps and target-side sub-plans
    /// both honour the timeout.
    /// </summary>
    public int TimeoutSeconds { get; set; }

    /// <summary>
    /// M14.4 Start Trigger: wait for the previous step or run alongside
    /// it. Default <see cref="StepStartTrigger.StartAfterPrevious"/>.
    /// </summary>
    public StepStartTrigger StartTrigger { get; set; } = StepStartTrigger.StartAfterPrevious;
}
