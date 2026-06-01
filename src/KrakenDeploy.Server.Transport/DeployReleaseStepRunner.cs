using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Octostache;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// Step config keys for an <c>Octopus.DeployRelease</c> step, mirroring
/// Octopus's <c>Octopus.Action.DeployRelease.*</c> namespace exactly.
/// Sourced from the Octopus public docs and verified against the real
/// Argosy deploymentprocess export (clean-room — not from Calamari source).
/// </summary>
public static class OctopusDeployReleaseConfigKeys
{
    private const string Prefix = "Octopus.Action.DeployRelease.";

    /// <summary>
    /// Required. Identifier of the child project to deploy. Resolved at
    /// runtime against the active Space — accepted forms are Kraken project
    /// <see cref="KrakenDeploy.Server.Core.Domain.Projects.Project.Id"/>
    /// (GUID, the Kraken-native form),
    /// <see cref="KrakenDeploy.Server.Core.Domain.Projects.Project.Slug"/>,
    /// or <see cref="KrakenDeploy.Server.Core.Domain.Projects.Project.Name"/>
    /// (case-insensitive). Imported Octopus exports typically carry the
    /// Octopus-style "Projects-NN" id which the user must remap when
    /// importing.
    /// </summary>
    public const string ProjectId = Prefix + "ProjectId";

    /// <summary>
    /// Optional, default <c>Always</c>. Controls when the child deployment
    /// runs:
    /// <list type="bullet">
    ///   <item><c>Always</c> — always trigger a new child deployment.</item>
    ///   <item><c>IfNewer</c> / <c>IfNotCurrent</c> — only when the latest
    ///     release of the child project is newer than what is currently
    ///     deployed to this environment.</item>
    /// </list>
    /// <c>IfChannelHasChanged</c> and tenant-aware conditions are not yet
    /// honoured — the runner logs a warning and treats them as <c>Always</c>.
    /// </summary>
    public const string DeploymentCondition = Prefix + "DeploymentCondition";
}

/// <summary>
/// Server-side orchestrator step. Triggered by <see cref="DeploymentWorker"/>
/// when it encounters a step with <c>StepType = "Octopus.DeployRelease"</c>
/// in a server-side group. Reads the project + deployment-condition properties,
/// resolves the child project, picks the latest release, decides whether to
/// trigger a new child deployment per the condition, then orchestrates:
/// <list type="number">
///   <item>Create the child deployment via <see cref="DeploymentService.CreateAsync"/>
///         against the parent's environment and target. Record the parent
///         link via <see cref="Deployment.ParentDeploymentId"/>.</item>
///   <item>Poll the child's <see cref="DeploymentLogEntry"/> rows; for each
///         new entry, append a prefixed line to the *parent* deployment's
///         log (so the operator sees the child's progress without leaving
///         the parent's view).</item>
///   <item>Wait for the child's status to become a terminal state
///         (<c>Succeeded</c> / <c>Failed</c> / <c>Cancelled</c>). Return
///         <c>true</c> only on <c>Succeeded</c>.</item>
/// </list>
/// </summary>
public sealed class DeployReleaseStepRunner(
    IServiceScopeFactory scopeFactory,
    IHubContext<UiHub, IUiHubClient> uiHub,
    TimeProvider timeProvider,
    ILogger<DeployReleaseStepRunner> logger)
{
    /// <summary>
    /// Step type the worker dispatches to this runner.
    /// </summary>
    public const string StepType = "Octopus.DeployRelease";

    /// <summary>
    /// Polling interval for the child-log mirror loop. Half-second cadence
    /// keeps the parent log close to real-time without hammering the DB.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    public async Task<bool> ExecuteAsync(
        Guid parentDeploymentId,
        DeploymentStepPlan step,
        IReadOnlyDictionary<string, string> planVariables,
        CancellationToken ct)
    {
        await AppendLogAsync(parentDeploymentId, "info",
            $"--- Step {step.Index + 1}: {step.Name} (Octopus.DeployRelease) ---", ct)
            .ConfigureAwait(false);

        // Resolve config (Octostache-substituted).
        var octostache = BuildOctostache(planVariables);
        var rawProjectId  = step.Config.GetValueOrDefault(OctopusDeployReleaseConfigKeys.ProjectId);
        var projectIdRef  = string.IsNullOrWhiteSpace(rawProjectId) ? "" : octostache.Evaluate(rawProjectId);
        var conditionRaw  = step.Config.GetValueOrDefault(OctopusDeployReleaseConfigKeys.DeploymentCondition);
        var condition     = NormaliseCondition(conditionRaw, out var conditionWarning);

        if (conditionWarning is not null)
        {
            await AppendLogAsync(parentDeploymentId, "warning", conditionWarning, ct).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(projectIdRef))
        {
            await AppendLogAsync(parentDeploymentId, "error",
                $"Octopus.DeployRelease is missing required key '{OctopusDeployReleaseConfigKeys.ProjectId}'.",
                ct).ConfigureAwait(false);
            return false;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();

        // ── Resolve the parent deployment to find environment + target set ──
        // M-RollingDeployments Phase 1b: include the join collection so the
        // child cascade inherits the parent's FULL target set (not just the
        // legacy single TargetId). Operator-facing semantic: if you cascade
        // from a multi-target parent, the child runs against the same set
        // of targets — preserving the "release X to environment Y" intent
        // across the cascade boundary.
        var parent = await db.Deployments
            .AsNoTracking()
            .Include(d => d.Targets)
            .FirstOrDefaultAsync(d => d.Id == parentDeploymentId, ct)
            .ConfigureAwait(false);
        if (parent is null)
        {
            await AppendLogAsync(parentDeploymentId, "error",
                "Parent deployment row vanished mid-step.", ct).ConfigureAwait(false);
            return false;
        }
        if (parent.TargetId is null)
        {
            await AppendLogAsync(parentDeploymentId, "error",
                "Parent deployment has no target — Octopus.DeployRelease requires a target.", ct)
                .ConfigureAwait(false);
            return false;
        }

        // Build the additional target id set from the join collection
        // (excluding the primary TargetId which CreateAsync re-adds).
        var parentAdditionalTargetIds = parent.Targets
            .Select(a => a.TargetId)
            .Where(id => id != parent.TargetId.Value)
            .ToList();

        // ── Resolve the child project (Guid -> Slug -> Name) ────────────────
        var childProject = await ResolveProjectAsync(db, projectIdRef, parent.SpaceId, ct)
            .ConfigureAwait(false);
        if (childProject is null)
        {
            await AppendLogAsync(parentDeploymentId, "error",
                $"Octopus.DeployRelease: could not resolve a project for '{projectIdRef}'. " +
                "Accepted forms are the Kraken project GUID, slug, or name. Octopus-native ids like " +
                "'Projects-21' need remapping — set the step's ProjectId to your local project's slug or id.",
                ct).ConfigureAwait(false);
            return false;
        }

        await AppendLogAsync(parentDeploymentId, "info",
            $"Octopus.DeployRelease: resolved child project '{childProject.Name}' " +
            $"(slug '{childProject.Slug}', id {childProject.Id}). Condition: {condition}.",
            ct).ConfigureAwait(false);

        // ── Pick the latest release of the child project ────────────────────
        var latestRelease = await db.Releases
            .AsNoTracking()
            .Where(r => r.ProjectId == childProject.Id)
            .OrderByDescending(r => r.CreatedUtc)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (latestRelease is null)
        {
            await AppendLogAsync(parentDeploymentId, "error",
                $"Octopus.DeployRelease: child project '{childProject.Name}' has no releases.",
                ct).ConfigureAwait(false);
            return false;
        }

        // ── Apply the deployment condition (skip if we don't need a new deploy) ─
        var shouldDeploy = await EvaluateConditionAsync(
            db, condition, childProject.Id, latestRelease, parent.EnvironmentId, ct)
            .ConfigureAwait(false);
        if (!shouldDeploy)
        {
            await AppendLogAsync(parentDeploymentId, "info",
                $"Octopus.DeployRelease: condition '{condition}' is satisfied — " +
                $"release '{latestRelease.Version}' is already current. Skipping child deployment.",
                ct).ConfigureAwait(false);
            return true;
        }

        // ── Create the child deployment, link it to the parent, dispatch ─────
        Deployment child;
        try
        {
            var deploymentService = scope.ServiceProvider.GetRequiredService<DeploymentService>();
            child = await deploymentService.CreateAsync(
                releaseId:           latestRelease.Id,
                environmentId:       parent.EnvironmentId,
                targetId:            parent.TargetId.Value,
                tenantId:            parent.TenantId,
                scheduledFor:        null,
                additionalTargetIds: parentAdditionalTargetIds,
                ct:                  ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await AppendLogAsync(parentDeploymentId, "error",
                $"Octopus.DeployRelease: failed to create child deployment: {ex.Message}",
                ct).ConfigureAwait(false);
            return false;
        }

        await using (var linkScope = scopeFactory.CreateAsyncScope())
        {
            var linkDb = linkScope.ServiceProvider.GetRequiredService<KrakenDbContext>();
            var tracked = await linkDb.Deployments.FindAsync([child.Id], ct).ConfigureAwait(false);
            if (tracked is not null)
            {
                tracked.ParentDeploymentId = parentDeploymentId;
                await linkDb.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }

        await AppendLogAsync(parentDeploymentId, "info",
            $"Octopus.DeployRelease: created child deployment {child.Id} for release '{latestRelease.Version}'. " +
            "Mirroring child log into parent until completion…",
            ct).ConfigureAwait(false);

        // ── Mirror the child's log into the parent + wait for terminal state ──
        return await WaitForChildAsync(child.Id, parentDeploymentId, step.Name, ct).ConfigureAwait(false);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private async Task<bool> WaitForChildAsync(
        Guid childId, Guid parentId, string stepName, CancellationToken ct)
    {
        var lastSequence = -1;
        while (!ct.IsCancellationRequested)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();

            // Stream any new log lines the child has written since we last polled.
            var newLines = await db.DeploymentLogEntries
                .AsNoTracking()
                .Where(l => l.DeploymentId == childId && l.Sequence > lastSequence)
                .OrderBy(l => l.Sequence)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var line in newLines)
            {
                await AppendLogAsync(parentId, line.Level,
                    $"[{stepName} -> {childId.ToString("N")[..8]}] {line.Message}", ct)
                    .ConfigureAwait(false);
                lastSequence = line.Sequence;
            }

            var status = await db.Deployments
                .AsNoTracking()
                .Where(d => d.Id == childId)
                .Select(d => (DeploymentStatus?)d.Status)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            if (status is null)
            {
                await AppendLogAsync(parentId, "error",
                    $"Octopus.DeployRelease: child deployment {childId} disappeared.", ct)
                    .ConfigureAwait(false);
                return false;
            }

            switch (status.Value)
            {
                case DeploymentStatus.Succeeded:
                    await AppendLogAsync(parentId, "info",
                        $"Octopus.DeployRelease: child deployment {childId.ToString("N")[..8]} succeeded.",
                        ct).ConfigureAwait(false);
                    return true;
                case DeploymentStatus.Failed:
                    await AppendLogAsync(parentId, "error",
                        $"Octopus.DeployRelease: child deployment {childId.ToString("N")[..8]} failed.",
                        ct).ConfigureAwait(false);
                    return false;
                case DeploymentStatus.Cancelled:
                    await AppendLogAsync(parentId, "error",
                        $"Octopus.DeployRelease: child deployment {childId.ToString("N")[..8]} was cancelled.",
                        ct).ConfigureAwait(false);
                    return false;
                default:
                    // Queued / Running — keep polling.
                    break;
            }

            try
            {
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                return false;
            }
        }
        return false;
    }

    private static async Task<KrakenDeploy.Server.Core.Domain.Projects.Project?> ResolveProjectAsync(
        KrakenDbContext db, string idRef, Guid spaceId, CancellationToken ct)
    {
        // 1. Guid
        if (Guid.TryParse(idRef, out var asGuid))
        {
            var byId = await db.Projects.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == asGuid && p.SpaceId == spaceId, ct)
                .ConfigureAwait(false);
            if (byId is not null) { return byId; }
        }

        // 2. Slug
        var bySlug = await db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Slug == idRef && p.SpaceId == spaceId, ct)
            .ConfigureAwait(false);
        if (bySlug is not null) { return bySlug; }

        // 3. Name (case-insensitive, exact match) — uses Postgres ILIKE so the
        // comparison happens in-database. An exact-match ILIKE with no wildcard
        // characters degenerates to a case-insensitive equality.
        var byName = await db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p =>
                EF.Functions.ILike(p.Name, idRef) && p.SpaceId == spaceId, ct)
            .ConfigureAwait(false);
        return byName;
    }

    private static string NormaliseCondition(string? raw, out string? warning)
    {
        warning = null;
        if (string.IsNullOrWhiteSpace(raw)) { return "Always"; }
        var trimmed = raw.Trim();
        switch (trimmed.ToLowerInvariant())
        {
            case "always":         return "Always";
            case "ifnewer":
            case "ifnotcurrent":   return "IfNewer";
            case "ifchannelhaschanged":
                warning =
                    $"Octopus.DeployRelease: DeploymentCondition '{trimmed}' is not yet honoured — " +
                    "the runner will trigger a child deployment unconditionally (Always).";
                return "Always";
            default:
                warning =
                    $"Octopus.DeployRelease: unrecognised DeploymentCondition '{trimmed}' — " +
                    "defaulting to 'Always'.";
                return "Always";
        }
    }

    private static async Task<bool> EvaluateConditionAsync(
        KrakenDbContext db, string condition, Guid childProjectId, Release latestRelease,
        Guid environmentId, CancellationToken ct)
    {
        if (condition == "Always") { return true; }

        // IfNewer: trigger only if the latest release of the child project is
        // *not* already deployed (successfully) to this environment.
        if (condition == "IfNewer")
        {
            var latestSuccessfulInEnv = await db.Deployments
                .AsNoTracking()
                .Where(d => d.EnvironmentId == environmentId
                         && d.Release.ProjectId == childProjectId
                         && d.Status == DeploymentStatus.Succeeded)
                .OrderByDescending(d => d.CompletedUtc)
                .Select(d => d.ReleaseId)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            return latestSuccessfulInEnv != latestRelease.Id;
        }

        return true;
    }

    private static VariableDictionary BuildOctostache(IReadOnlyDictionary<string, string> variables)
    {
        var dict = new VariableDictionary();
        foreach (var (k, v) in variables) { dict.Set(k, v); }
        return dict;
    }

    private async Task AppendLogAsync(
        Guid deploymentId, string level, string message, CancellationToken ct)
    {
        var timestamp = timeProvider.GetUtcNow();

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();
        var deployment = await db.Deployments.FindAsync([deploymentId], ct).ConfigureAwait(false);
        if (deployment is null)
        {
            logger.LogWarning(
                "DeployReleaseStepRunner: deployment {Id} not found for log line.", deploymentId);
            return;
        }

        var seq = deployment.NextLogSequence++;
        db.DeploymentLogEntries.Add(new DeploymentLogEntry
        {
            DeploymentId = deploymentId,
            Sequence     = seq,
            Timestamp    = timestamp,
            Message      = message,
            Level        = level,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await uiHub.Clients.Group($"deployment:{deploymentId}")
            .DeploymentLogAppendedAsync(deploymentId, seq, timestamp, level, message)
            .ConfigureAwait(false);
    }
}
