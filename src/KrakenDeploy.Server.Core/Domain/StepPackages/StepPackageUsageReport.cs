namespace KrakenDeploy.Server.Core.Domain.StepPackages;

/// <summary>
/// Conflict report returned when uninstalling a step-package version
/// (Phase D-11) is blocked by existing references. Lists every live
/// step (deployment process + runbook process) and every release
/// snapshot that still pins the version.
/// <para>
/// The UI presents this so the admin can pick a path forward: cancel,
/// edit the live steps to a different version (D-7), bulk-upgrade
/// them all (D-10), or accept that released snapshots will block the
/// uninstall until those releases are deleted / retention-pruned.
/// </para>
/// </summary>
public sealed record StepPackageUsageReport(
    string Name,
    string Version,
    IReadOnlyList<StepPackageUsageReport.LiveStepRef> LiveSteps,
    IReadOnlyList<StepPackageUsageReport.ReleaseSnapshotRef> ReleaseSnapshots)
{
    /// <summary>
    /// A live (editable) step that still pins the version. The admin can
    /// open the project / runbook in the UI and bump the pin, or wait for
    /// the bulk-upgrade tool (D-10).
    /// </summary>
    public sealed record LiveStepRef(
        Guid StepId,
        string ProjectName,
        string ProjectSlug,
        string StepName,
        bool IsRunbook);

    /// <summary>
    /// A frozen release snapshot that pinned the version at release-creation
    /// time. Released snapshots are immutable by design — the only way to
    /// clear this reference is to delete the release.
    /// </summary>
    public sealed record ReleaseSnapshotRef(
        Guid ReleaseId,
        string ProjectName,
        string ProjectSlug,
        string ReleaseVersion);
}
