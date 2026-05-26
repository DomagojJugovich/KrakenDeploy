using System.Threading.Channels;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Runbooks;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Narrow interface exposing only the runbook-trigger surface that
/// external callers (e.g. the M13.B.2/3 Runbook subscription transport)
/// need. Keeps the dependency on RunbookService's broader CRUD API out
/// of consumer code + lets tests substitute a stub without touching the
/// runbook execution pipeline.
/// </summary>
public interface IRunbookTrigger
{
    Task<RunbookRun> TriggerAsync(
        Guid runbookId,
        Guid environmentId,
        Guid targetId,
        Guid? tenantId = null,
        CancellationToken ct = default);
}

/// <summary>
/// CRUD and dispatch for <see cref="Runbook"/>s, their process steps, and
/// <see cref="RunbookRun"/> executions.
/// </summary>
public class RunbookService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    RunbookRunChannel runbookQueue,
    StepPackageResolver? stepPackageResolver = null)
    : IRunbookTrigger
{
    // ── Runbook CRUD ───────────────────────────────────────────────────────────

    public async Task<List<Runbook>> GetAllAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Runbooks
            .Where(r => r.ProjectId == projectId)
            .OrderBy(r => r.Name)
            .ToListAsync(ct);
    }

    public async Task<Runbook?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Runbooks
            .Include(r => r.Process)
                .ThenInclude(p => p != null ? p.Steps.OrderBy(s => s.SortOrder) : null!)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    public async Task<Runbook> CreateAsync(
        Guid projectId, string name, string? description, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

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
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
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
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
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

    public async Task<RunbookStep> AddStepAsync(
        Guid runbookId,
        string name,
        string stepType,
        string packageId,
        List<string> targetRoles,
        Dictionary<string, string> config,
        string? stepPackageName = null,
        string? stepPackageVersion = null,
        Guid? parentStepId = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var process = await GetOrCreateProcessAsync(db, runbookId, ct).ConfigureAwait(false);

        // M15 — SortOrder is scoped to siblings (per-parent). Top-level
        // steps share one numbering; children of each group share their own.
        var siblings = process.Steps.Where(s => s.ParentStepId == parentStepId).ToList();
        var maxOrder = siblings.Count > 0 ? siblings.Max(s => s.SortOrder) : -1;

        var pin = await ResolvePinAsync(
                stepType, stepPackageName, stepPackageVersion, ct)
            .ConfigureAwait(false);

        var step = new RunbookStep
        {
            ProcessId          = process.Id,
            Name               = name,
            StepType           = stepType,
            PackageId          = packageId,
            TargetRoles        = targetRoles,
            Config             = config,
            SortOrder          = maxOrder + 1,
            StepPackageName    = pin?.Name,
            StepPackageVersion = pin?.Version,
            ParentStepId       = parentStepId,
        };

        db.RunbookSteps.Add(step);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // M15 — structural validation as defence in depth. Mirrors
        // ProcessService.EnsureValidAsync: throws ProcessValidationException
        // when the tree violates an invariant.
        await EnsureValidAsync(db, process.Id, ct).ConfigureAwait(false);

        return step;
    }

    public async Task<RunbookStep?> UpdateStepAsync(
        Guid stepId,
        string name,
        string stepType,
        string packageId,
        List<string> targetRoles,
        Dictionary<string, string> config,
        string? stepPackageName = null,
        string? stepPackageVersion = null,
        UpdateParent? updateParent = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var step = await db.RunbookSteps.FindAsync(new object?[] { stepId }, ct).ConfigureAwait(false);
        if (step is null)
        {
            return null;
        }

        step.Name        = name;
        step.StepType    = stepType;
        step.PackageId   = packageId;
        step.TargetRoles = targetRoles;
        step.Config      = config;

        if (stepPackageName is not null && stepPackageVersion is not null)
        {
            step.StepPackageName    = stepPackageName;
            step.StepPackageVersion = stepPackageVersion;
        }

        // M15 — parent reassignment. UpdateParent wrapper distinguishes
        // "don't touch" (default null) from "reparent to top-level (null)".
        // Drag-into-row follow-up: reassign SortOrder to be last among the
        // new siblings when the parent actually changes, matching the
        // "drop = append to end" UX convention.
        if (updateParent is not null
            && step.ParentStepId != updateParent.NewParentStepId)
        {
            step.ParentStepId = updateParent.NewParentStepId;
            var newSiblings = await db.RunbookSteps
                .Where(s => s.ProcessId == step.ProcessId
                            && s.ParentStepId == updateParent.NewParentStepId
                            && s.Id != step.Id)
                .ToListAsync(ct).ConfigureAwait(false);
            step.SortOrder = newSiblings.Count == 0
                ? 0
                : newSiblings.Max(s => s.SortOrder) + 1;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EnsureValidAsync(db, step.ProcessId, ct).ConfigureAwait(false);

        return step;
    }

    /// <summary>
    /// M15 — runs <see cref="ProcessValidator"/> against the current state
    /// of the runbook process's steps + throws <see cref="ProcessValidationException"/>
    /// when any rule is broken. Mirrors <c>ProcessService.EnsureValidAsync</c>.
    /// The validator works on <see cref="IComposableStep"/>, so the same
    /// rules apply to both deployment processes and runbooks.
    /// </summary>
    private static async Task EnsureValidAsync(
        KrakenDbContext db, Guid processId, CancellationToken ct)
    {
        var steps = await db.RunbookSteps
            .Where(s => s.ProcessId == processId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var result = ProcessValidator.Validate(steps);
        if (!result.IsValid)
        {
            throw new ProcessValidationException(result);
        }
    }

    /// <summary>
    /// Mirrors <c>ProcessService.ResolvePinAsync</c>: explicit (name, version)
    /// wins; otherwise asks the resolver for the highest semver claiming
    /// the step type. Returns null when no resolver is wired or no package
    /// claims the type.
    /// </summary>
    private async Task<StepPackagePin?> ResolvePinAsync(
        string stepType, string? explicitName, string? explicitVersion, CancellationToken ct)
    {
        if (explicitName is not null && explicitVersion is not null)
        {
            return new StepPackagePin(explicitName, explicitVersion);
        }
        if (stepPackageResolver is null)
        {
            return null;
        }
        return await stepPackageResolver
            .ResolveLatestForStepTypeAsync(stepType, ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteStepAsync(Guid stepId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
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
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var runbook = await db.Runbooks
            .Include(r => r.Process)
                .ThenInclude(p => p != null ? p.Steps.OrderBy(s => s.SortOrder) : null!)
            .FirstOrDefaultAsync(r => r.Id == runbookId, ct)
            .ConfigureAwait(false)
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
                // M15 — freeze the step's Id + parent link so the flattener
                // can walk the tree at run time. Runbooks support step
                // composition as of the M15 follow-up commit (ParentStepId
                // is now on RunbookStep + RunbookRunWorker pre-flattens
                // the tree via DeploymentPlanFlattener).
                Id                 = s.Id,
                ParentStepId       = s.ParentStepId,
                Name               = s.Name,
                StepType           = s.StepType,
                PackageId          = s.PackageId,
                PackageVersion     = "",   // runbooks don't pin package versions at run time
                TargetRoles        = [.. s.TargetRoles],
                Config             = new Dictionary<string, string>(s.Config),
                SortOrder          = s.SortOrder,
                // D-6: step-package pin is copied as-is from the live step.
                // Runbooks share the same per-step pin contract as releases.
                StepPackageName    = s.StepPackageName,
                StepPackageVersion = s.StepPackageVersion,
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

    public async Task<List<RunbookRun>> GetRunsAsync(Guid runbookId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.RunbookRuns
            .Where(r => r.RunbookId == runbookId)
            .Include(r => r.Environment)
            .Include(r => r.Target)
            .OrderByDescending(r => r.CreatedUtc)
            .ToListAsync(ct);
    }

    public async Task<RunbookRun?> GetRunAsync(Guid runId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.RunbookRuns
            .Include(r => r.Runbook)
            .Include(r => r.Environment)
            .Include(r => r.Target)
            .Include(r => r.LogEntries.OrderBy(l => l.Sequence))
            .FirstOrDefaultAsync(r => r.Id == runId, ct);
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private static async Task<RunbookProcess> GetOrCreateProcessAsync(
        KrakenDbContext db, Guid runbookId, CancellationToken ct)
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
}
