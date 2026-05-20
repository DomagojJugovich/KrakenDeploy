namespace KrakenDeploy.Server.Core.Domain.StepPackages;

/// <summary>
/// Live (editable) usage of a given step package, grouped by the pinned
/// version (Phase D-10). Powers the <c>/step-packages/{name}/usage</c>
/// admin page that drives the bulk-upgrade flow.
/// <para>
/// Each <see cref="VersionGroup"/> lists every <c>DeploymentStep</c> +
/// <c>RunbookStep</c> currently pinned to that exact version. Released
/// snapshots (<c>StepSnapshot</c>) are NOT included — they're permanent
/// by contract and the bulk-upgrade tool deliberately doesn't touch them.
/// </para>
/// </summary>
public sealed record StepPackageUsage(
    string PackageName,
    IReadOnlyList<StepPackageUsage.VersionGroup> Groups)
{
    public sealed record VersionGroup(
        string Version,
        IReadOnlyList<UsageRow> Rows);

    public sealed record UsageRow(
        Guid StepId,
        string ProjectName,
        string ProjectSlug,
        string StepName,
        string StepType,
        bool IsRunbook);
}

/// <summary>
/// Outcome of a bulk-upgrade run (Phase D-10). Touched count is the
/// number of steps whose pin was bumped; skipped lists rows that were
/// either already on the target version (no-op) or couldn't be found
/// (concurrent delete, etc.).
/// </summary>
public sealed record BulkUpgradeResult(
    string PackageName,
    string TargetVersion,
    int Touched,
    IReadOnlyList<BulkUpgradeResult.SkippedRow> Skipped)
{
    public sealed record SkippedRow(Guid StepId, string Reason);
}
