namespace KrakenDeploy.Server.Core.Domain.Releases;

/// <summary>
/// Immutable snapshot of a deployment/runbook step taken at release or run creation time.
/// Stored as jsonb so historical records remain accurate after process edits.
/// </summary>
public sealed class StepSnapshot
{
    public string Name { get; init; } = string.Empty;
    public string StepType { get; init; } = string.Empty;
    public string PackageId { get; init; } = string.Empty;
    public string PackageVersion { get; init; } = string.Empty;
    public List<string> TargetRoles { get; init; } = [];
    public Dictionary<string, string> Config { get; init; } = [];
    public int SortOrder { get; init; }

    /// <summary>The step-package name locked into this release (Phase D-6).
    /// Paired with <see cref="StepPackageVersion"/>; both null together when
    /// no installed package claimed the step type at snapshot time.</summary>
    public string? StepPackageName { get; init; }

    /// <summary>
    /// The step-package version locked into this release (Phase D-6).
    /// <c>null</c> when no step-package claimed this step type at snapshot
    /// time — the agent then uses its hardcoded handler. Once D-8 has
    /// extracted the built-ins, every new release pins a real (name, version)
    /// pair here.
    /// <para>
    /// Pin is permanent: even if newer versions of the step package land
    /// later, the release continues to deploy against this exact one. That's
    /// the contract that makes a release reproducible.
    /// </para>
    /// </summary>
    public string? StepPackageVersion { get; init; }
}
