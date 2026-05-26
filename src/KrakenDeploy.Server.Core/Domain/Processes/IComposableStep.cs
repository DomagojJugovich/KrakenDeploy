namespace KrakenDeploy.Server.Core.Domain.Processes;

/// <summary>
/// M15 — the minimal step contract <see cref="ProcessValidator"/> needs to
/// enforce the structural invariants of a process tree: identity,
/// parent link, type discriminator, Config bag, and human-readable name.
///
/// <para>
/// Implemented by both <see cref="DeploymentStep"/> (process steps) and
/// <see cref="Runbooks.RunbookStep"/> (runbook steps) so the same validator
/// + the same parent-child machinery works on both. The interface deliberately
/// does NOT carry execution knobs (Condition / Required / Retries / Timeout /
/// StartTrigger) — those live on the entity-specific surface, are validated
/// per-entity by the existing services, and don't affect tree integrity.
/// </para>
///
/// <para>
/// <see cref="IEnumerable{T}"/> covariance lets callers pass an
/// <c>IEnumerable&lt;DeploymentStep&gt;</c> or <c>IEnumerable&lt;RunbookStep&gt;</c>
/// directly to <see cref="ProcessValidator.Validate"/> without
/// materialising an intermediate projection.
/// </para>
/// </summary>
public interface IComposableStep
{
    /// <summary>Per-step identity. Validator uses it to detect cycles and
    /// resolve <see cref="ParentStepId"/> against the in-memory set.</summary>
    Guid Id { get; }

    /// <summary>The parent step within the same process / runbook. Null
    /// = top-level. The validator enforces that the referenced parent
    /// exists in the same set + that the type discriminator allows it.</summary>
    Guid? ParentStepId { get; set; }

    /// <summary>Human-readable name. Appears verbatim in validation error
    /// messages and audit details.</summary>
    string Name { get; }

    /// <summary>Step-type discriminator. The validator special-cases
    /// <see cref="KrakenStepTypes.StepGroup"/> as the one type allowed to
    /// have children + checks for leaf-only Config keys when the type IS
    /// a Step Group.</summary>
    string StepType { get; }

    /// <summary>Configuration bag. The validator inspects keys (not values)
    /// to detect leaf-only keys on Step Groups.</summary>
    Dictionary<string, string> Config { get; }

    /// <summary>The owning process or runbook-process row. Used by the
    /// editor to look up sibling candidates for the Parent dropdown.</summary>
    Guid ProcessId { get; }

    /// <summary>Logical package ID that this step deploys (may be empty
    /// for steps that don't consume a package — IIS-only configure steps,
    /// runbook script steps, Step Groups).</summary>
    string PackageId { get; }

    /// <summary>Roles filter — only targets whose roles overlap this list
    /// run the step. Empty = run on every target.</summary>
    List<string> TargetRoles { get; }

    /// <summary>Phase D-6 pinned step-package name. <c>null</c> when no
    /// installed package claims the step type.</summary>
    string? StepPackageName { get; }

    /// <summary>Phase D-6 pinned step-package version. Paired with
    /// <see cref="StepPackageName"/>; both null or both set.</summary>
    string? StepPackageVersion { get; }

    /// <summary>Zero-based execution order within the step's parent
    /// (top-level steps share one numbering; children of each group
    /// share their own).</summary>
    int SortOrder { get; }
}
