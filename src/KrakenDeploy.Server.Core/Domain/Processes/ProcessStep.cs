using KrakenDeploy.Execution;
using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Processes;

/// <summary>
/// A single step in a <see cref="Process"/> — the one <c>process_steps</c> table
/// replacing the old <c>deployment_steps</c> + <c>runbook_steps</c> pair. Carries
/// the FULL execution-knob set for both owner kinds (runbook steps used to lack
/// Condition/Required/retries/timeout/StartTrigger and stored <c>target_roles</c>
/// as jsonb — both are unified here: <c>text[]</c> roles, all knobs present).
/// <para>
/// The Kraken-native script step type is <c>"Kraken.Script"</c>; imported Octopus
/// processes use <c>"Octopus.Script"</c>. Step-type-specific configuration is
/// stored in <see cref="Config"/> as a string-keyed dictionary (jsonb).
/// </para>
/// </summary>
public class ProcessStep : Entity, IComposableStep, ISpaceScoped
{
    /// <summary>Inherited from the owning process; stamped on insert so by-stepId
    /// reads/mutations are Space-safe. Platform-wide scans (step-package usage)
    /// must use IgnoreQueryFilters.</summary>
    public Guid SpaceId { get; set; }

    public Guid ProcessId { get; set; }
    public Process Process { get; set; } = null!;

    public required string Name { get; set; }

    /// <summary>Step type identifier, e.g. "Kraken.Script".</summary>
    public required string StepType { get; set; }

    /// <summary>Zero-based execution order within the parent (top-level steps share
    /// one numbering; each group's children share their own).</summary>
    public int SortOrder { get; set; }

    /// <summary>Only targets whose roles overlap this list execute the step. Empty
    /// = run on all targets in the task.</summary>
    public List<string> TargetRoles { get; set; } = [];

    /// <summary>The logical package ID this step deploys (may be empty for steps
    /// that don't consume a package). The version is chosen at release-creation.</summary>
    public string PackageId { get; set; } = "";

    /// <summary>Step-type-specific configuration stored as jsonb.</summary>
    public Dictionary<string, string> Config { get; set; } = [];

    /// <summary>Pinned step-package name that supplies the handler (Phase D-6);
    /// <c>null</c> when no installed package claims the step type.</summary>
    public string? StepPackageName { get; set; }

    /// <summary>Pinned step-package version paired with <see cref="StepPackageName"/>
    /// (both null or both set).</summary>
    public string? StepPackageVersion { get; set; }

    // ── M14 step-execution knobs (full set, both owner kinds) ────────────────

    /// <summary>Run Condition: when should this step run based on prior outcomes?
    /// Default <see cref="StepCondition.Success"/>.</summary>
    public StepCondition Condition { get; set; } = StepCondition.Success;

    /// <summary>Octostache expression evaluated when <see cref="Condition"/> is
    /// <see cref="StepCondition.Variable"/>. Truthy -> run; falsy/unresolved -> skip.</summary>
    public string? ConditionVariableExpression { get; set; }

    /// <summary>When <c>true</c> (default), a step failure aborts the task; when
    /// <c>false</c>, the task is marked as having failures but the loop continues.</summary>
    public bool Required { get; set; } = true;

    /// <summary>Additional attempts after the first failure. <c>0</c> disables retries.</summary>
    public int MaxRetries { get; set; }

    /// <summary>Delay between retry attempts, in seconds. Honoured only when
    /// <see cref="MaxRetries"/> &gt; 0.</summary>
    public int RetryDelaySeconds { get; set; }

    /// <summary>Per-step timeout in seconds. <c>0</c> (default) = unlimited.</summary>
    public int TimeoutSeconds { get; set; }

    /// <summary>Wait for the previous step or run alongside it. Default
    /// <see cref="StepStartTrigger.StartAfterPrevious"/>.</summary>
    public StepStartTrigger StartTrigger { get; set; } = StepStartTrigger.StartAfterPrevious;

    // ── D3 control-flow flags (promoted from jsonb Config) ───────────────────
    // The orchestrator branches on these typed columns, never the raw Config
    // dict. The Octopus-compatible string keys survive only at the import/export
    // boundary (see KrakenStepGroupConfigKeys / KrakenScriptConfigKeys.RunOnServer).

    /// <summary>Leaf/script flag: when <c>true</c>, the step runs in the server
    /// process instead of being dispatched to an agent. Read by
    /// <c>WavePartitioner.IsServerStep</c>. Meaningful only on leaf steps —
    /// a <see cref="KrakenStepTypes.StepGroup"/> must not set it (validator-enforced).</summary>
    public bool RunOnServer { get; set; }

    /// <summary>Step-group flag: rolling-window fan-out cap. When set to a
    /// positive integer, the orchestrator batches the group's target-side waves
    /// to at most this many targets at a time. <c>null</c> = no cap. Only valid
    /// on a <see cref="KrakenStepTypes.StepGroup"/> and must be positive
    /// (validator-enforced at save time).</summary>
    public int? MaxParallelism { get; set; }

    /// <summary>Step-group flag: name (or Octostache expression) of the array
    /// variable this Step Group iterates as a ForEach loop. <c>null</c>/blank =
    /// plain container. Stored as the UNRESOLVED template; the flattener
    /// substitutes it at deploy time. Only valid on a
    /// <see cref="KrakenStepTypes.StepGroup"/>.</summary>
    public string? ForEachCollection { get; set; }

    /// <summary>Step-group flag: when <c>true</c>, ForEach iterations dispatch
    /// together as one parallel wave. Only valid on a
    /// <see cref="KrakenStepTypes.StepGroup"/>.</summary>
    public bool ForEachParallel { get; set; }

    // ── M15 step composition (child steps + ForEach) ─────────────────────────

    /// <summary>When set, marks this step as a child of another step in the same
    /// <see cref="Process"/>. Only <see cref="KrakenStepTypes.StepGroup"/> steps
    /// may have children. Self-FK, <c>ON DELETE CASCADE</c>. Null for top-level.</summary>
    public Guid? ParentStepId { get; set; }

    /// <summary>Navigation to the parent step. Null for top-level steps.</summary>
    public ProcessStep? Parent { get; set; }

    /// <summary>Navigation to child steps. Empty for leaf-type steps.</summary>
    public ICollection<ProcessStep> Children { get; set; } = [];
}
