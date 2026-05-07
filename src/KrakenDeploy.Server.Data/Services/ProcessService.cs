using KrakenDeploy.Server.Core.Domain.Processes;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Manages the deployment process (ordered step list) for a project.
/// </summary>
public class ProcessService(IDbContextFactory<KrakenDbContext> dbFactory)
{
    // ── Get / create ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns the deployment process for the project, creating an empty one if it
    /// does not exist yet.
    /// </summary>
    public async Task<DeploymentProcess> GetOrCreateAsync(
        Guid projectId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await GetOrCreateCoreAsync(db, projectId, ct).ConfigureAwait(false);
    }

    /// <summary>Returns the process with steps, or null if the project has none.</summary>
    public async Task<DeploymentProcess?> GetAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.DeploymentProcesses
            .Include(p => p.Steps.OrderBy(s => s.SortOrder))
            .FirstOrDefaultAsync(p => p.ProjectId == projectId, ct);
    }

    // ── Steps ──────────────────────────────────────────────────────────────

    /// <summary>Appends a new step to the end of the process.</summary>
    public async Task<DeploymentStep> AddStepAsync(
        Guid projectId,
        string name,
        string stepType,
        string packageId,
        List<string> targetRoles,
        Dictionary<string, string> config,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var process = await GetOrCreateCoreAsync(db, projectId, ct).ConfigureAwait(false);

        var maxSort = await db.DeploymentSteps
            .Where(s => s.ProcessId == process.Id)
            .Select(s => (int?)s.SortOrder)
            .MaxAsync(ct)
            .ConfigureAwait(false) ?? -1;

        var step = new DeploymentStep
        {
            ProcessId = process.Id,
            Name = name,
            StepType = stepType,
            PackageId = packageId,
            TargetRoles = targetRoles,
            Config = config,
            SortOrder = maxSort + 1,
        };

        db.DeploymentSteps.Add(step);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return step;
    }

    /// <summary>Updates the mutable fields of an existing step.</summary>
    public async Task<DeploymentStep?> UpdateStepAsync(
        Guid stepId,
        string name,
        string packageId,
        List<string> targetRoles,
        Dictionary<string, string> config,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var step = await db.DeploymentSteps.FindAsync([stepId], ct).ConfigureAwait(false);
        if (step is null)
        {
            return null;
        }

        step.Name = name;
        step.PackageId = packageId;
        step.TargetRoles = targetRoles;
        step.Config = config;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return step;
    }

    /// <summary>
    /// Moves the step one position up or down in the process. <paramref name="direction"/>
    /// is <c>-1</c> for up, <c>+1</c> for down. No-op if the step is already at the edge.
    /// </summary>
    public async Task<bool> MoveStepAsync(Guid stepId, int direction, CancellationToken ct = default)
    {
        if (direction != -1 && direction != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(direction), "Must be -1 (up) or +1 (down).");
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var step = await db.DeploymentSteps.FindAsync([stepId], ct).ConfigureAwait(false);
        if (step is null)
        {
            return false;
        }

        var siblings = await db.DeploymentSteps
            .Where(s => s.ProcessId == step.ProcessId)
            .OrderBy(s => s.SortOrder)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var index = siblings.FindIndex(s => s.Id == stepId);
        var swapWith = index + direction;
        if (swapWith < 0 || swapWith >= siblings.Count)
        {
            return false; // Already at the edge.
        }

        (siblings[index].SortOrder, siblings[swapWith].SortOrder)
            = (siblings[swapWith].SortOrder, siblings[index].SortOrder);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Removes a step and re-sequences the remaining steps.</summary>
    public async Task<bool> RemoveStepAsync(Guid stepId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var step = await db.DeploymentSteps
            .Include(s => s.Process)
            .FirstOrDefaultAsync(s => s.Id == stepId, ct)
            .ConfigureAwait(false);

        if (step is null)
        {
            return false;
        }

        var processId = step.ProcessId;
        db.DeploymentSteps.Remove(step);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Re-sequence remaining steps.
        var remaining = await db.DeploymentSteps
            .Where(s => s.ProcessId == processId)
            .OrderBy(s => s.SortOrder)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        for (var i = 0; i < remaining.Count; i++)
        {
            remaining[i].SortOrder = i;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    // ── Private helpers ────────────────────────────────────────────────────

    private static async Task<DeploymentProcess> GetOrCreateCoreAsync(
        KrakenDbContext db, Guid projectId, CancellationToken ct)
    {
        var process = await db.DeploymentProcesses
            .Include(p => p.Steps)
            .FirstOrDefaultAsync(p => p.ProjectId == projectId, ct)
            .ConfigureAwait(false);

        if (process is not null)
        {
            return process;
        }

        process = new DeploymentProcess { ProjectId = projectId };
        db.DeploymentProcesses.Add(process);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return process;
    }
}
