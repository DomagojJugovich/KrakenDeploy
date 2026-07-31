using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.StepPackages;

/// <summary>
/// The step-type REGISTRY (SC2 / SD-1): one row per known step type — the
/// single authoring authority for what the picker shows, what schema the
/// editor renders, and where a type executes. Rows with
/// <see cref="StepTypeEntrySource.Package"/> are derived from installed
/// <see cref="StepPackage"/>s and rewritten on every install/uninstall/seed
/// (never hand-edited); <see cref="StepTypeEntrySource.System"/> rows are the
/// two package-less types (<c>Kraken.StepGroup</c>, <c>Octopus.DeployRelease</c>),
/// seeded by migration and maintained by the registry rebuild.
/// <para>
/// System-wide like <see cref="StepPackage"/> (not <c>ISpaceScoped</c>).
/// Presets (<c>StepTemplate</c> rows) reference a type by its ActionType
/// string, not by FK — a preset whose type has no registry row is importable
/// but unrunnable until the serving package installs.
/// </para>
/// </summary>
public class StepTypeEntry : AuditableEntity
{
    /// <summary>Lower-cased step-type id (e.g. <c>octopus.healthcheck</c>). Unique.</summary>
    public required string TypeId { get; set; }

    /// <summary>Picker-card title. Falls back to the serving package's DisplayName when the manifest entry carried no metadata.</summary>
    public required string DisplayName { get; set; }

    /// <summary>Small taxonomy bucket (e.g. <c>kubernetes</c>) feeding the picker's category filters.</summary>
    public string? Category { get; set; }

    /// <summary>One-liner shown on the picker card.</summary>
    public string? Description { get; set; }

    /// <summary>Surfaced in the picker's Featured section.</summary>
    public bool Featured { get; set; }

    /// <summary>Where steps of this type execute — drives wave partitioning and the RunOnServer guard.</summary>
    public required StepTypeExecutionLocus ExecutionLocus { get; set; }

    /// <summary>Package-derived or System (package-less) row.</summary>
    public required StepTypeEntrySource Source { get; set; }

    /// <summary>
    /// Name of the installed package that serves this type — the highest
    /// semver claimer, matching <c>StepPackageResolver</c>'s pin choice.
    /// <c>null</c> for System rows.
    /// </summary>
    public string? ServingPackageName { get; set; }

    /// <summary>Cached serving version (kept in step with <see cref="ServingPackageName"/> by the rebuild).</summary>
    public string? ServingPackageVersion { get; set; }
}

/// <summary>Where steps of a type execute.</summary>
public enum StepTypeExecutionLocus
{
    /// <summary>Runs on the agent via the serving step package's handler.</summary>
    AgentPackage = 0,

    /// <summary>Runs server-side via a dedicated runner (e.g. <c>Octopus.DeployRelease</c>).</summary>
    ServerRunner = 1,

    /// <summary>Structural marker with no handler at all (<c>Kraken.StepGroup</c>).</summary>
    Structural = 2,
}

/// <summary>How a registry row came to exist.</summary>
public enum StepTypeEntrySource
{
    /// <summary>Derived from an installed step package's manifest; rewritten on install/uninstall/seed.</summary>
    Package = 0,

    /// <summary>Seeded for the package-less types; not tied to any install.</summary>
    System = 1,
}
