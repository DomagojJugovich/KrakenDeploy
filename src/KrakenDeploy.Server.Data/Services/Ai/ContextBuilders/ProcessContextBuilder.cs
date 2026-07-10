using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Data.Services.Ai.Curators;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services.Ai.ContextBuilders;

/// <summary>
/// Builds the LLM-shaped <see cref="ProcessContextDto"/> for a project's
/// live deployment process or a release's frozen snapshot (M11.B). Both
/// shapes funnel through one projection so the AI sees an identical
/// structure whether it's reasoning about the current editable process or
/// a historical release.
/// <para>
/// The shared kernel: the MCP <c>kraken://.../process</c> Resource calls
/// this (behind a permission gate + audit); the M11.C diagnosis job calls
/// it directly (system context, no gate). One projection, no drift.
/// </para>
/// </summary>
public sealed class ProcessContextBuilder(
    IDbContextFactory<KrakenDbContext> dbFactory,
    StepConfigCuratorRegistry curators)
{
    /// <summary>
    /// Builds the context for a project's LIVE deployment process, resolved
    /// by slug. Returns <c>null</c> when no project matches the slug (caller
    /// maps that to a 404 / not-found resource).
    /// </summary>
    public async Task<ProcessContextDto?> BuildForProjectAsync(
        string projectSlug, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectSlug);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var project = await db.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Slug == projectSlug, ct)
            .ConfigureAwait(false);
        if (project is null)
        {
            return null;
        }

        var steps = await db.Processes
            .AsNoTracking()
            .Where(p => p.OwnerKind == ProcessOwnerKind.Project && p.OwnerId == project.Id)
            .SelectMany(p => p.Steps)
            .OrderBy(s => s.SortOrder)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var nameById = steps.ToDictionary(s => s.Id, s => s.Name);
        var stepDtos = steps
            .Select((s, index) => Project(
                index:        index,
                id:           s.Id,
                name:         s.Name,
                stepType:     s.StepType,
                roles:        s.TargetRoles,
                required:     s.Required,
                startTrigger: s.StartTrigger,
                parentStepId: s.ParentStepId,
                config:       s.Config,
                nameById:     nameById,
                fullConfigUri: $"kraken://projects/{projectSlug}/process/steps/{index}/config"))
            .ToList();

        return new ProcessContextDto(
            ProjectName:    project.Name,
            ReleaseVersion: null,
            Steps:          stepDtos);
    }

    /// <summary>
    /// Builds the context for a release's FROZEN process snapshot, resolved
    /// by project slug + version. Returns <c>null</c> when no matching
    /// release exists.
    /// </summary>
    public async Task<ProcessContextDto?> BuildForReleaseAsync(
        string projectSlug, string version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var release = await db.Releases
            .AsNoTracking()
            .Include(r => r.Project)
            .FirstOrDefaultAsync(r => r.Project.Slug == projectSlug && r.Version == version, ct)
            .ConfigureAwait(false);
        if (release is null)
        {
            return null;
        }

        var snapshot = release.ProcessSnapshot.OrderBy(s => s.SortOrder).ToList();
        var nameById = snapshot
            .Where(s => s.Id != Guid.Empty)
            .ToDictionary(s => s.Id, s => s.Name);

        var stepDtos = snapshot
            .Select((s, index) => Project(
                index:        index,
                id:           s.Id,
                name:         s.Name,
                stepType:     s.StepType,
                roles:        s.TargetRoles,
                required:     s.Required,
                startTrigger: s.StartTrigger,
                parentStepId: s.ParentStepId,
                config:       s.Config,
                nameById:     nameById,
                fullConfigUri: $"kraken://releases/{projectSlug}/{version}/steps/{index}/config"))
            .ToList();

        return new ProcessContextDto(
            ProjectName:    release.Project.Name,
            ReleaseVersion: release.Version,
            Steps:          stepDtos);
    }

    private ProcessStepContextDto Project(
        int index,
        Guid id,
        string name,
        string stepType,
        IReadOnlyList<string> roles,
        bool required,
        StepStartTrigger startTrigger,
        Guid? parentStepId,
        IReadOnlyDictionary<string, string> config,
        Dictionary<Guid, string> nameById,
        string fullConfigUri)
    {
        string? parentName = null;
        if (parentStepId is { } pid && pid != Guid.Empty)
        {
            nameById.TryGetValue(pid, out parentName);
        }

        return new ProcessStepContextDto(
            Index:         index,
            Name:          name,
            StepType:      stepType,
            TargetRoles:   roles.ToArray(),
            Required:      required,
            IsServerSide:  IsServerSide(stepType, config),
            StartTrigger:  startTrigger.ToString(),
            ParentName:    parentName,
            ConfigSummary: curators.Curate(stepType, config),
            FullConfigUri: fullConfigUri);
    }

    // Mirrors WavePartitioner.IsServerStep (Server.Transport) — duplicated
    // here because Server.Data must not reference Server.Transport. The
    // rule is small + stable: a step runs server-side when it carries
    // Octopus.Action.RunOnServer="true" OR its type is an intrinsically
    // server-only orchestrator type (currently just DeployRelease).
    private static bool IsServerSide(string stepType, IReadOnlyDictionary<string, string> config)
    {
        if (config.TryGetValue("Octopus.Action.RunOnServer", out var v)
            && string.Equals(v, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return string.Equals(stepType, "Octopus.DeployRelease", StringComparison.OrdinalIgnoreCase);
    }
}
