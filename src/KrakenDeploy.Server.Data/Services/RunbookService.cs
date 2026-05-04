using System.Threading.Channels;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Runbooks;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// CRUD and dispatch for <see cref="Runbook"/>s, their process steps, and
/// <see cref="RunbookRun"/> executions.
/// </summary>
public class RunbookService(KrakenDbContext db, RunbookRunChannel runbookQueue)
{
    // ── Runbook CRUD ───────────────────────────────────────────────────────────

    public Task<List<Runbook>> GetAllAsync(Guid projectId, CancellationToken ct = default)
        => db.Runbooks
            .Where(r => r.ProjectId == projectId)
            .OrderBy(r => r.Name)
            .ToListAsync(ct);

    public Task<Runbook?> GetAsync(Guid id, CancellationToken ct = default)
        => db.Runbooks
            .Include(r => r.Process)
                .ThenInclude(p => p != null ? p.Steps.OrderBy(s => s.SortOrder) : null!)
            .FirstOrDefaultAsync(r => r.Id == id, ct);

    public async Task<Runbook> CreateAsync(
        Guid projectId, string name, string? description, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var projectExists = await db.Projects.AnyAsync(p => p.Id == projectId, ct).ConfigureAwait(false);
        if (!projectExists)
        {
            throw new InvalidOperationException($"Project {projectId} not found.");
        }

        if (await db.Runbooks.AnyAsync(r => r.ProjectId == projectId && r.Name == name, ct)
            .ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Runbook '{name}' already exists for this project.");
        }

        var runbook = new Runbook { ProjectId = projectId, Name = name, Description = description };
        db.Runbooks.Add(runbook);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return runbook;
    }

    public async Task<Runbook?> UpdateAsync(
        Guid id, string name, string? description, CancellationToken ct = default)
    {
        var runbook = await db.Runbooks.FindAsync(new object?[] { id }, ct).ConfigureAwait(false);
        if (runbook is null)
        {
            return null;
        }

        runbook.Name = name;
        runbook.Description = description;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return runbook;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var runbook = await db.Runbooks.FindAsync(new object?[] { id }, ct).ConfigureAwait(false);
        if (runbook is null)
        {
            return false;
        }

        db.Runbooks.Remove(runbook);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    // ── Step management ────────────────────────────────────────────────────────

    private async Task<RunbookProcess> GetOrCreateProcessAsync(Guid runbookId, CancellationToken ct)
    {
        var process = await db.RunbookProcesses
            .Include(p => p.Steps)
            .FirstOrDefaultAsync(p => p.RunbookId == runbookId, ct)
            .ConfigureAwait(false);

        if (process is not null)
        {
            return process;
        }

        process = new RunbookProcess { RunbookId = runbookId };
        db.RunbookProcesses.Add(process);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return process;
    }

    public async Task<RunbookStep> AddStepAsync(
        Guid runbookId,
        string name,
        string stepType,
        string packageId,
        List<string> targetRoles,
        Dictionary<string, string> config,
        CancellationToken ct = default)
    {
        var process = await GetOrCreateProcessAsync(runbookId, ct).ConfigureAwait(false);

        var maxOrder = process.Steps.Count > 0
            ? process.Steps.Max(s => s.SortOrder)
            : -1;

        var step = new RunbookStep
        {
            ProcessId = process.Id,
            Name = name,
            StepType = stepType,
            PackageId = packageId,
            TargetRoles = targetRoles,
            Config = config,
            SortOrder = maxOrder + 1,
        };

        db.RunbookSteps.Add(step);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return step;
    }

    public async Task<RunbookStep?> UpdateStepAsync(
        Guid stepId,
        string name,
        string stepType,
        string packageId,
        List<string> targetRoles,
        Dictionary<string, string> config,
        CancellationToken ct = default)
    {
        var step = await db.RunbookSteps.FindAsync(new object?[] { stepId }, ct).ConfigureAwait(false);
        if (step is null)
        {
            return null;
        }

        step.Name = name;
        step.StepType = stepType;
        step.PackageId = packageId;
        step.TargetRoles = targetRoles;
        step.Config = config;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return step;
    }

    public async Task<bool> DeleteStepAsync(Guid stepId, CancellationToken ct = default)
    {
        var step = await db.RunbookSteps.FindAsync(new object?[] { stepId }, ct).ConfigureAwait(false);
        if (step is null)
        {
            return false;
        }

        db.RunbookSteps.Remove(step);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    // ── Dispatch ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="RunbookRun"/> by snapping the current runbook process,
    /// then enqueues it for dispatch to the target agent.
    /// </summary>
    public async Task<RunbookRun> TriggerAsync(
        Guid runbookId,
        Guid environmentId,
        Guid targetId,
        Guid? tenantId = null,
        CancellationToken ct = default)
    {
        var runbook = await GetAsync(runbookId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Runbook {runbookId} not found.");

        if (runbook.Process is null || runbook.Process.Steps.Count == 0)
        {
            throw new InvalidOperationException("Runbook has no steps. Add at least one step before triggering a run.");
        }

        var envExists = await db.Environments.AnyAsync(e => e.Id == environmentId, ct).ConfigureAwait(false);
        if (!envExists)
        {
            throw new InvalidOperationException($"Environment {environmentId} not found.");
        }

        // Snapshot the process (mirrors what ReleaseService does for releases).
        var snapshot = runbook.Process.Steps
            .OrderBy(s => s.SortOrder)
            .Select(s => new StepSnapshot
            {
                Name = s.Name,
                StepType = s.StepType,
                PackageId = s.PackageId,
                PackageVersion = "",   // runbooks don't pin package versions at run time
                TargetRoles = [.. s.TargetRoles],
                Config = new Dictionary<string, string>(s.Config),
                SortOrder = s.SortOrder,
            })
            .ToList();

        var run = new RunbookRun
        {
            RunbookId = runbookId,
            EnvironmentId = environmentId,
            TargetId = targetId,
            TenantId = tenantId,
            Status = DeploymentStatus.Queued,
            ProcessSnapshot = snapshot,
        };

        db.RunbookRuns.Add(run);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await runbookQueue.Writer.WriteAsync(run.Id, ct).ConfigureAwait(false);

        return run;
    }

    // ── Query runs ─────────────────────────────────────────────────────────────

    public Task<List<RunbookRun>> GetRunsAsync(Guid runbookId, CancellationToken ct = default)
        => db.RunbookRuns
            .Where(r => r.RunbookId == runbookId)
            .Include(r => r.Environment)
            .Include(r => r.Target)
            .OrderByDescending(r => r.CreatedUtc)
            .ToListAsync(ct);

    public Task<RunbookRun?> GetRunAsync(Guid runId, CancellationToken ct = default)
        => db.RunbookRuns
            .Include(r => r.Runbook)
            .Include(r => r.Environment)
            .Include(r => r.Target)
            .Include(r => r.LogEntries.OrderBy(l => l.Sequence))
            .FirstOrDefaultAsync(r => r.Id == runId, ct);
}
