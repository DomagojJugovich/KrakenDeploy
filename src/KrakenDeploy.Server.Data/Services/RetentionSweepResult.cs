namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Per-category outcome of one <see cref="RetentionService.RunSweepAsync"/> pass.
/// Every counter reflects what the pass DID — or, in dry-run mode, what it
/// WOULD have done (the sweep computes the full prune set, logs it, and deletes
/// nothing). The scheduled job flattens this into the
/// <c>Retention.SweepCompleted</c> audit summary so an operator reading the audit
/// log sees one line per category without having to correlate job logs.
/// </summary>
public sealed record RetentionSweepResult
{
    /// <summary>Deployments pruned by the lifecycle phase keep-window.</summary>
    public int Deployments { get; init; }

    /// <summary>Releases pruned (outside every phase's release keep-window AND
    /// unreferenced by any deployment).</summary>
    public int Releases { get; init; }

    /// <summary>Package versions pruned (beyond the per-package keep count and not
    /// pinned by a retained release / deployment).</summary>
    public int Packages { get; init; }

    /// <summary>Runbook runs pruned by the per-(runbook, environment) keep count.</summary>
    public int RunbookRuns { get; init; }

    /// <summary>task_step_logs blob rows pruned by the age cap.</summary>
    public int StepLogBlobs { get; init; }

    /// <summary>Orphaned task_log_live staging rows swept (live lines whose parent
    /// task is already terminal — they should have been compacted away).</summary>
    public int OrphanLiveLogs { get; init; }

    /// <summary>On-disk artifact files deleted (inline with their pruned task, or
    /// swept when orphaned by a previously-pruned row).</summary>
    public int ArtifactFiles { get; init; }

    /// <summary>On-disk offline drop-bundle files deleted.</summary>
    public int DropBundleFiles { get; init; }

    /// <summary>True when the pass ran in dry-run mode and deleted nothing.</summary>
    public bool DryRun { get; init; }

    public bool IsEmpty =>
        Deployments + Releases + Packages + RunbookRuns + StepLogBlobs +
        OrphanLiveLogs + ArtifactFiles + DropBundleFiles == 0;

    /// <summary>One-line "category=count" summary for the audit Details column.</summary>
    public string ToSummary() =>
        $"dryRun={DryRun}, deployments={Deployments}, releases={Releases}, " +
        $"packages={Packages}, runbookRuns={RunbookRuns}, stepLogBlobs={StepLogBlobs}, " +
        $"orphanLiveLogs={OrphanLiveLogs}, artifactFiles={ArtifactFiles}, " +
        $"dropBundleFiles={DropBundleFiles}";

    public static RetentionSweepResult operator +(RetentionSweepResult a, RetentionSweepResult b) =>
        new()
        {
            Deployments     = a.Deployments + b.Deployments,
            Releases        = a.Releases + b.Releases,
            Packages        = a.Packages + b.Packages,
            RunbookRuns     = a.RunbookRuns + b.RunbookRuns,
            StepLogBlobs    = a.StepLogBlobs + b.StepLogBlobs,
            OrphanLiveLogs  = a.OrphanLiveLogs + b.OrphanLiveLogs,
            ArtifactFiles   = a.ArtifactFiles + b.ArtifactFiles,
            DropBundleFiles = a.DropBundleFiles + b.DropBundleFiles,
            DryRun          = a.DryRun || b.DryRun,
        };
}

/// <summary>
/// Resolved knobs for one sweep pass. The job builds this from
/// <c>PerformanceSettings</c> + the per-runbook overrides + the dry-run feature
/// flag, so <see cref="RetentionService.RunSweepAsync"/> stays settings-agnostic
/// and unit-testable (tests pass the knobs directly).
/// </summary>
public sealed record RetentionSweepOptions
{
    /// <summary>Package versions kept per package id; &lt;= 0 disables package pruning.</summary>
    public int PackageKeepVersions { get; init; }

    /// <summary>Instance-wide default runbook-run keep; a per-runbook
    /// <c>Runbook.RetentionKeepRuns</c> override wins when &gt; 0. &lt;= 0 disables.</summary>
    public int RunbookRunKeep { get; init; }

    /// <summary>Age cap (days) for task_step_logs blobs; &lt;= 0 disables age pruning
    /// (the orphan task_log_live sweep still runs).</summary>
    public int TaskLogAgeDays { get; init; }

    /// <summary>When true, compute + log the prune set but delete nothing.</summary>
    public bool DryRun { get; init; }
}
