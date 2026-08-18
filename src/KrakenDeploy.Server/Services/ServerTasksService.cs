using System.Text;
using Hangfire;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Runbooks;
using KrakenDeploy.Server.Data.Services;

namespace KrakenDeploy.Server.Services;

/// <summary>
/// Discriminates the three task sources the global <c>/tasks</c> page unifies.
/// </summary>
public enum ServerTaskKind
{
    Deployment,
    RunbookRun,
    SystemJob,
}

/// <summary>
/// Display-level task state. Maps both <see cref="DeploymentStatus"/> (deployments
/// + runbook runs) and Hangfire job states onto one vocabulary so the Tasks grid
/// renders a single badge column.
/// </summary>
public enum ServerTaskState
{
    Queued,
    Scheduled,
    Running,
    Succeeded,
    SucceededWithWarnings,
    Failed,
    Cancelled,
    PendingOfflineResult,

    /// <summary>WP3 — parked at a manual-intervention gate awaiting a human
    /// approve/reject. Non-terminal and operator-actionable.</summary>
    Paused,

    Unknown,
}

/// <summary>
/// One row in the unified Tasks list. A flat projection over a
/// <see cref="Deployment"/>, a <see cref="RunbookRun"/>, or a Hangfire job
/// execution — whichever produced it (see <see cref="Kind"/>).
/// </summary>
public sealed record ServerTaskRow
{
    public required string Key { get; init; }
    public required ServerTaskKind Kind { get; init; }
    public required ServerTaskState State { get; init; }
    public required string Title { get; init; }

    public string? Project { get; init; }
    public string? Environment { get; init; }
    public string? Target { get; init; }
    public string? Tenant { get; init; }

    /// <summary>Denormalized initiator display (who/what created the task); null for
    /// system/Hangfire rows that have no task provenance.</summary>
    public string? InitiatedBy { get; init; }

    /// <summary>Provenance cause; null for system/Hangfire rows.</summary>
    public ServerTaskCause? Cause { get; init; }

    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? CompletedUtc { get; init; }
    public DateTimeOffset? QueuedUtc { get; init; }

    /// <summary>
    /// The underlying <c>server_tasks</c> id for a DB-backed row (deployment or runbook
    /// run); <c>null</c> for a Hangfire system-job row, which has no task. Lets the page
    /// probe for pending manual-intervention gates without re-parsing
    /// <see cref="Key"/>.
    /// </summary>
    public Guid? TaskId { get; init; }

    /// <summary>In-app route (deployment / runbook) or Hangfire dashboard URL; null = not navigable.</summary>
    public string? DetailUrl { get; init; }

    /// <summary>True when <see cref="DetailUrl"/> leaves the Blazor SPA (Hangfire dashboard) and needs a full load.</summary>
    public bool ExternalNav { get; init; }

    /// <summary>Sort key — most recent activity first.</summary>
    public DateTimeOffset SortUtc =>
        CompletedUtc ?? StartedUtc ?? QueuedUtc ?? DateTimeOffset.MinValue;
}

/// <summary>
/// Aggregates deployments, runbook runs, and Hangfire system-job executions into a
/// single time-ordered list for the global Tasks page (Octopus-parity). Deployments
/// and runbook runs come from the DB (Space-scoped); system jobs come from Hangfire's
/// read-only monitoring API (instance-wide). Scoped so it can compose the scoped
/// <see cref="DeploymentService"/> / <see cref="RunbookService"/> directly.
/// </summary>
public sealed class ServerTasksService(
    DeploymentService deployments,
    RunbookService runbooks,
    ILogger<ServerTasksService> logger)
{
    // Bounds on the Hangfire pull. Recurring jobs (subscription poller, scheduled-
    // dispatch, digest flush) fire every minute, so the succeeded bucket is the
    // noisy one; cap it and let the page's Kind filter isolate real deployments.
    private const int MaxProcessing = 100;
    private const int MaxScheduled = 100;
    private const int MaxSucceeded = 200;
    private const int MaxFailed = 200;

    /// <summary>
    /// Friendly labels for the recurring jobs registered in
    /// <c>HangfireJobRegistrar</c>. Keyed by the job class name. Anything not
    /// listed falls back to a humanised type name.
    /// </summary>
    private static readonly Dictionary<string, string> JobLabels = new(StringComparer.Ordinal)
    {
        ["AuditRetentionJob"] = "Apply audit retention",
        ["AiCallLogRetentionJob"] = "Apply AI call-log retention",
        ["AgentLastSeenOfflineJob"] = "Mark stale agents offline",
        ["RegistrationTokenExpiryJob"] = "Expire registration tokens",
        ["ScheduledDeploymentDispatchJob"] = "Dispatch scheduled deployments",
        ["InterruptionTimeoutJob"] = "Expire manual-intervention gates",
        ["StepTemplateCatalogPollJob"] = "Poll step-template catalog",
        ["StepPackageCatalogPollJob"] = "Poll step-package catalog",
        ["SubscriptionPollerJob"] = "Process subscriptions",
        ["EmailDigestFlushJob"] = "Flush email digests",
        ["BackupJob"] = "Run scheduled backup",
        ["DeploymentDiagnosisJob"] = "Diagnose failed deployment",
    };

    /// <summary>
    /// Most-recent rows pulled per source. The Tasks page shows recent activity,
    /// not full history — bounding the DB pull keeps the page O(1) in instance age.
    /// </summary>
    private const int RecentRowCap = 500;

    /// <summary>
    /// The unified task list, optionally narrowed to one machine. The
    /// <paramref name="targetId"/> filter (F6 — the Tasks page's <c>?target=</c>
    /// query) resolves through the <c>task_target_assignments</c> join in the DB
    /// services — the single authority for a task's target set. A row's
    /// <c>Title</c>/<c>Target</c> strings never reliably contain machine names
    /// (multi-target rows collapse to "N targets"), so a string match would be
    /// wrong. System jobs carry no target and are omitted from a filtered read.
    /// </summary>
    public async Task<List<ServerTaskRow>> GetTasksAsync(
        Guid? targetId = null, CancellationToken ct = default)
    {
        var rows = new List<ServerTaskRow>();

        var deps = targetId is { } tid
            ? await deployments.GetForTargetAsync(tid, limit: RecentRowCap, ct: ct)
            : await deployments.GetAllAsync(limit: RecentRowCap, ct: ct);
        rows.AddRange(deps.Select(ToRow));

        var runs = targetId is { } tid2
            ? await runbooks.GetRunsForTargetAsync(tid2, limit: RecentRowCap, ct: ct)
            : await runbooks.GetAllRunsAsync(limit: RecentRowCap, ct: ct);
        rows.AddRange(runs.Select(ToRow));

        // Hangfire is best-effort: a storage hiccup must never blank the whole
        // page (deployments + runbook runs are the operator's primary signal).
        // Skipped under a target filter — system jobs run on no machine.
        if (targetId is null)
        {
            try
            {
                rows.AddRange(ReadSystemJobs());
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to read Hangfire system jobs for the Tasks page");
            }
        }

        return rows
            .OrderByDescending(r => r.SortUtc)
            .ToList();
    }

    // ── Deployment / runbook projections ─────────────────────────────────────

    private static ServerTaskRow ToRow(Deployment d)
    {
        var project = d.Release?.Project?.Name;
        var version = d.Release?.Version;
        var env = d.Environment?.Name;
        var tenant = d.Tenant?.Name;

        var sb = new StringBuilder("Deploy ");
        sb.Append(project ?? "release");
        if (!string.IsNullOrEmpty(version)) { sb.Append(" release ").Append(version); }
        if (!string.IsNullOrEmpty(env)) { sb.Append(" to ").Append(env); }
        if (!string.IsNullOrEmpty(tenant)) { sb.Append(" for ").Append(tenant); }

        return new ServerTaskRow
        {
            Key = $"dep:{d.Id}",
            TaskId = d.Id,
            Kind = ServerTaskKind.Deployment,
            State = MapStatus(d.Status),
            Title = sb.ToString(),
            Project = project,
            Environment = env,
            Target = d.TargetNames() is { Count: > 0 } names
                ? (names.Count == 1 ? names[0] : $"{names.Count} targets")
                : null,
            Tenant = tenant,
            StartedUtc = d.StartedUtc,
            CompletedUtc = d.CompletedUtc,
            QueuedUtc = d.ScheduledFor ?? d.CreatedUtc,
            InitiatedBy = d.CreatedByDisplay,
            Cause = d.Cause,
            DetailUrl = $"/deployments/{d.Id}",
        };
    }

    private static ServerTaskRow ToRow(RunbookRun r)
    {
        var runbook = r.Runbook?.Name;
        var project = r.Runbook?.Project?.Name;
        var env = r.Environment?.Name;
        var tenant = r.Tenant?.Name;

        var sb = new StringBuilder("Run runbook ");
        sb.Append(runbook ?? "(unnamed)");
        if (!string.IsNullOrEmpty(env)) { sb.Append(" on ").Append(env); }
        if (!string.IsNullOrEmpty(tenant)) { sb.Append(" for ").Append(tenant); }

        return new ServerTaskRow
        {
            Key = $"run:{r.Id}",
            TaskId = r.Id,
            Kind = ServerTaskKind.RunbookRun,
            State = MapStatus(r.Status),
            Title = sb.ToString(),
            Project = project,
            Environment = env,
            Target = r.TargetLabel(),
            Tenant = tenant,
            StartedUtc = r.StartedUtc,
            CompletedUtc = r.CompletedUtc,
            QueuedUtc = r.CreatedUtc,
            InitiatedBy = r.CreatedByDisplay,
            Cause = r.Cause,
            // Deep-link to the run's own detail page (RunbookRunDetail) — parity
            // with deployment rows, which link to their detail page.
            DetailUrl = $"/runbook-runs/{r.Id}",
        };
    }

    private static ServerTaskState MapStatus(DeploymentStatus status) => status switch
    {
        DeploymentStatus.Queued => ServerTaskState.Queued,
        DeploymentStatus.Running => ServerTaskState.Running,
        DeploymentStatus.Succeeded => ServerTaskState.Succeeded,
        DeploymentStatus.SucceededWithWarnings => ServerTaskState.SucceededWithWarnings,
        DeploymentStatus.Failed => ServerTaskState.Failed,
        DeploymentStatus.Cancelled => ServerTaskState.Cancelled,
        DeploymentStatus.PendingOfflineResult => ServerTaskState.PendingOfflineResult,
        DeploymentStatus.Paused => ServerTaskState.Paused,
        _ => ServerTaskState.Unknown,
    };

    // ── Hangfire system jobs ─────────────────────────────────────────────────

    private static List<ServerTaskRow> ReadSystemJobs()
    {
        var api = JobStorage.Current.GetMonitoringApi();
        var rows = new List<ServerTaskRow>();

        foreach (var (id, dto) in api.ProcessingJobs(0, MaxProcessing))
        {
            rows.Add(SystemRow(id, dto.Job, ServerTaskState.Running,
                startedUtc: Utc(dto.StartedAt)));
        }

        foreach (var (id, dto) in api.FailedJobs(0, MaxFailed))
        {
            rows.Add(SystemRow(id, dto.Job, ServerTaskState.Failed,
                completedUtc: Utc(dto.FailedAt)));
        }

        foreach (var (id, dto) in api.SucceededJobs(0, MaxSucceeded))
        {
            var completed = Utc(dto.SucceededAt);
            DateTimeOffset? started = completed is { } c && dto.TotalDuration is { } ms
                ? c - TimeSpan.FromMilliseconds(ms)
                : null;
            rows.Add(SystemRow(id, dto.Job, ServerTaskState.Succeeded,
                startedUtc: started, completedUtc: completed));
        }

        foreach (var (id, dto) in api.ScheduledJobs(0, MaxScheduled))
        {
            rows.Add(SystemRow(id, dto.Job, ServerTaskState.Scheduled,
                queuedUtc: Utc(dto.ScheduledAt) ?? new DateTimeOffset(
                    DateTime.SpecifyKind(dto.EnqueueAt, DateTimeKind.Utc), TimeSpan.Zero)));
        }

        return rows;
    }

    private static ServerTaskRow SystemRow(
        string jobId,
        global::Hangfire.Common.Job? job,
        ServerTaskState state,
        DateTimeOffset? startedUtc = null,
        DateTimeOffset? completedUtc = null,
        DateTimeOffset? queuedUtc = null)
        => new()
        {
            Key = $"job:{jobId}",
            Kind = ServerTaskKind.SystemJob,
            State = state,
            Title = LabelFor(job),
            StartedUtc = startedUtc,
            CompletedUtc = completedUtc,
            QueuedUtc = queuedUtc,
            // Job-level detail lives on the Hangfire dashboard (SystemAdmin-gated).
            DetailUrl = $"/hangfire/jobs/details/{jobId}",
            ExternalNav = true,
        };

    private static string LabelFor(global::Hangfire.Common.Job? job)
    {
        var typeName = job?.Type?.Name;
        if (string.IsNullOrEmpty(typeName))
        {
            return "System job";
        }

        return JobLabels.TryGetValue(typeName, out var label) ? label : Humanize(typeName);
    }

    /// <summary>"AgentLastSeenOfflineJob" → "Agent last seen offline".</summary>
    private static string Humanize(string typeName)
    {
        if (typeName.EndsWith("Job", StringComparison.Ordinal) && typeName.Length > 3)
        {
            typeName = typeName[..^3];
        }

        var sb = new StringBuilder(typeName.Length + 8);
        for (var i = 0; i < typeName.Length; i++)
        {
            var c = typeName[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(typeName[i - 1]))
            {
                sb.Append(' ').Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(i == 0 ? c : char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }

    private static DateTimeOffset? Utc(DateTime? dt) =>
        dt.HasValue
            ? new DateTimeOffset(DateTime.SpecifyKind(dt.Value, DateTimeKind.Utc), TimeSpan.Zero)
            : null;
}
