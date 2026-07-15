using System.Threading.Channels;
using KrakenDeploy.Server.Core.Domain.Accounts;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Runbooks;
using KrakenDeploy.Server.Core.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Narrow interface exposing only the runbook-trigger surface that external callers
/// (e.g. the Runbook subscription transport) need.
/// </summary>
public interface IRunbookTrigger
{
    Task<RunbookRun> TriggerAsync(
        Guid runbookId,
        Guid environmentId,
        Guid targetId,
        TaskInitiator initiator,
        CallerAuthorization caller,
        Guid? tenantId = null,
        CancellationToken ct = default);
}

/// <summary>
/// CRUD and dispatch for <see cref="Runbook"/>s, their process steps (unified
/// <see cref="Process"/> / <see cref="ProcessStep"/> tables, owner = Runbook), and
/// <see cref="RunbookRun"/> executions. Runbook runs now share the deployment
/// orchestrator: they carry the full M14 execution knobs, fan out over a target
/// assignment set, and gain artifacts / output variables / step outcomes.
/// </summary>
public class RunbookService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    RunbookRunChannel runbookQueue,
    TimeProvider time,
    IAccountContext accountContext,
    IPermissionEvaluator permissions,
    StepPackageResolver? stepPackageResolver = null)
    : IRunbookTrigger, IStepEditingHost
{
    // ── IStepEditingHost ───────────────────────────────────────────────
    // Runbook steps now carry the full M14 execution knobs (parity with the
    // deployment process editor), so the unified StepFormDialog shows its
    // Execution card and the knobs are persisted rather than discarded.

    bool IStepEditingHost.SupportsExecutionKnobs => true;

    async Task<Guid> IStepEditingHost.AddStepAsync(
        Guid containerId, string name, string stepType, string packageId,
        List<string> targetRoles, Dictionary<string, string> config,
        string? stepPackageName, string? stepPackageVersion,
        StepExecutionKnobs? knobs, Guid? parentStepId,
        CallerAuthorization caller, CancellationToken ct)
    {
        var step = await AddStepAsync(
            containerId, name, stepType, packageId, targetRoles, config, caller,
            stepPackageName, stepPackageVersion, knobs, parentStepId, ct)
            .ConfigureAwait(false);
        return step.Id;
    }

    async Task IStepEditingHost.UpdateStepAsync(
        Guid stepId, string name, string packageId,
        List<string> targetRoles, Dictionary<string, string> config,
        string? stepPackageName, string? stepPackageVersion,
        StepExecutionKnobs? knobs, UpdateParent? updateParent,
        CallerAuthorization caller, CancellationToken ct)
    {
        await UpdateStepAsync(
            stepId, name, packageId, targetRoles, config, caller,
            stepPackageName, stepPackageVersion, knobs, updateParent, ct)
            .ConfigureAwait(false);
    }

    async Task<IReadOnlyList<IComposableStep>> IStepEditingHost.GetProcessStepsAsync(
        Guid processId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var steps = await db.ProcessSteps
            .Where(s => s.ProcessId == processId)
            .OrderBy(s => s.SortOrder)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return [.. steps];
    }

    async Task<Guid?> IStepEditingHost.ResolveProjectIdAsync(
        Guid? containerId, Guid? processId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        // Container id on the runbook editor = runbook id; resolve to ProjectId.
        if (containerId is not null)
        {
            var runbook = await db.Runbooks
                .FirstOrDefaultAsync(r => r.Id == containerId.Value, ct)
                .ConfigureAwait(false);
            return runbook?.ProjectId;
        }
        if (processId is null) { return null; }

        // Edit path: process -> runbook (owner) -> project.
        var process = await db.Processes
            .FirstOrDefaultAsync(p => p.Id == processId.Value, ct)
            .ConfigureAwait(false);
        if (process is not { OwnerKind: ProcessOwnerKind.Runbook })
        {
            return null;
        }
        return await db.Runbooks
            .Where(r => r.Id == process.OwnerId)
            .Select(r => (Guid?)r.ProjectId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    // ── T1-8 authoritative scope check ───────────────────────────────────────
    // Runbook step editing is scoped to the runbook's owning project (permission
    // RunbookEdit). Resolve filter-free so a foreign-Space id fails closed.

    private async Task EnsureRunbookScopeAsync(
        KrakenDbContext db, CallerAuthorization caller, Guid runbookId, CancellationToken ct)
    {
        if (caller.IsSystem)
        {
            return;
        }
        var rb = await db.Runbooks.IgnoreQueryFilters()
            .Where(r => r.Id == runbookId)
            .Select(r => new { r.SpaceId, r.ProjectId })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        await permissions.EnsureScopedAsync(
            caller, Permission.RunbookEdit,
            new PermissionScope(SpaceId: rb?.SpaceId, ProjectId: rb?.ProjectId), ct)
            .ConfigureAwait(false);
    }

    // By-step-id: resolve the owning runbook's project from the step so an edit is
    // authorized against the step's REAL project (closes the by-step-id IDOR).
    private async Task EnsureStepScopeAsync(
        KrakenDbContext db, CallerAuthorization caller, ProcessStep step, CancellationToken ct)
    {
        if (caller.IsSystem)
        {
            return;
        }
        var projectId = await (
            from p in db.Processes.IgnoreQueryFilters()
            where p.Id == step.ProcessId && p.OwnerKind == ProcessOwnerKind.Runbook
            join r in db.Runbooks.IgnoreQueryFilters() on p.OwnerId equals r.Id
            select (Guid?)r.ProjectId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        await permissions.EnsureScopedAsync(
            caller, Permission.RunbookEdit,
            new PermissionScope(SpaceId: step.SpaceId, ProjectId: projectId), ct)
            .ConfigureAwait(false);
    }

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
        return await db.Runbooks.FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    /// <summary>The runbook's editable process steps (owner = Runbook), ordered.
    /// Empty when the runbook has no process yet.</summary>
    public async Task<List<ProcessStep>> GetProcessStepsAsync(
        Guid runbookId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var process = await db.Processes
            .Include(p => p.Steps.OrderBy(s => s.SortOrder))
            .FirstOrDefaultAsync(
                p => p.OwnerKind == ProcessOwnerKind.Runbook && p.OwnerId == runbookId, ct);
        return process is null ? [] : [.. process.Steps];
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
        var runbook = await db.Runbooks.FindAsync([id], ct).ConfigureAwait(false);
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
        var runbook = await db.Runbooks.FindAsync([id], ct).ConfigureAwait(false);
        if (runbook is null)
        {
            return false;
        }

        // The process is polymorphic (no owner FK) — delete it + its steps
        // explicitly so deleting a runbook doesn't orphan its process.
        var process = await db.Processes
            .FirstOrDefaultAsync(
                p => p.OwnerKind == ProcessOwnerKind.Runbook && p.OwnerId == id, ct)
            .ConfigureAwait(false);
        if (process is not null)
        {
            db.Processes.Remove(process); // cascades to its ProcessSteps
        }

        db.Runbooks.Remove(runbook);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    // ── Step management ────────────────────────────────────────────────────────

    public async Task<ProcessStep> AddStepAsync(
        Guid runbookId,
        string name,
        string stepType,
        string packageId,
        List<string> targetRoles,
        Dictionary<string, string> config,
        CallerAuthorization caller,
        string? stepPackageName = null,
        string? stepPackageVersion = null,
        StepExecutionKnobs? knobs = null,
        Guid? parentStepId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await EnsureRunbookScopeAsync(db, caller, runbookId, ct).ConfigureAwait(false);
        var process = await GetOrCreateProcessAsync(db, runbookId, ct).ConfigureAwait(false);

        var siblings = process.Steps.Where(s => s.ParentStepId == parentStepId).ToList();
        var maxOrder = siblings.Count > 0 ? siblings.Max(s => s.SortOrder) : -1;

        var pin = await ResolvePinAsync(
                stepType, stepPackageName, stepPackageVersion, ct)
            .ConfigureAwait(false);

        var k = knobs ?? StepExecutionKnobs.Default;
        var step = new ProcessStep
        {
            ProcessId                   = process.Id,
            Name                        = name,
            StepType                    = stepType,
            PackageId                   = packageId,
            TargetRoles                 = targetRoles,
            Config                      = config,
            SortOrder                   = maxOrder + 1,
            StepPackageName             = pin?.Name,
            StepPackageVersion          = pin?.Version,
            Condition                   = k.Condition,
            ConditionVariableExpression = k.ConditionVariableExpression,
            Required                    = k.Required,
            MaxRetries                  = k.MaxRetries,
            RetryDelaySeconds           = k.RetryDelaySeconds,
            TimeoutSeconds              = k.TimeoutSeconds,
            StartTrigger                = k.StartTrigger,
            ParentStepId                = parentStepId,
        };

        db.ProcessSteps.Add(step);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EnsureValidAsync(db, process.Id, ct).ConfigureAwait(false);

        return step;
    }

    public async Task<ProcessStep?> UpdateStepAsync(
        Guid stepId,
        string name,
        string packageId,
        List<string> targetRoles,
        Dictionary<string, string> config,
        CallerAuthorization caller,
        string? stepPackageName = null,
        string? stepPackageVersion = null,
        StepExecutionKnobs? knobs = null,
        UpdateParent? updateParent = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var step = await db.ProcessSteps.FindAsync([stepId], ct).ConfigureAwait(false);
        if (step is null)
        {
            return null;
        }

        await EnsureStepScopeAsync(db, caller, step, ct).ConfigureAwait(false);

        step.Name        = name;
        step.PackageId   = packageId;
        step.TargetRoles = targetRoles;
        step.Config      = config;

        if (stepPackageName is not null && stepPackageVersion is not null)
        {
            step.StepPackageName    = stepPackageName;
            step.StepPackageVersion = stepPackageVersion;
        }

        if (knobs is not null)
        {
            step.Condition                   = knobs.Condition;
            step.ConditionVariableExpression = knobs.ConditionVariableExpression;
            step.Required                    = knobs.Required;
            step.MaxRetries                  = knobs.MaxRetries;
            step.RetryDelaySeconds           = knobs.RetryDelaySeconds;
            step.TimeoutSeconds              = knobs.TimeoutSeconds;
            step.StartTrigger                = knobs.StartTrigger;
        }

        if (updateParent is not null
            && step.ParentStepId != updateParent.NewParentStepId)
        {
            step.ParentStepId = updateParent.NewParentStepId;
            var newSiblings = await db.ProcessSteps
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

    private static async Task EnsureValidAsync(
        KrakenDbContext db, Guid processId, CancellationToken ct)
    {
        var steps = await db.ProcessSteps
            .Where(s => s.ProcessId == processId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var result = ProcessValidator.Validate(steps);
        if (!result.IsValid)
        {
            throw new ProcessValidationException(result);
        }
    }

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

    public async Task<bool> DeleteStepAsync(
        Guid stepId, CallerAuthorization caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var step = await db.ProcessSteps.FindAsync([stepId], ct).ConfigureAwait(false);
        if (step is null)
        {
            return false;
        }

        await EnsureStepScopeAsync(db, caller, step, ct).ConfigureAwait(false);

        db.ProcessSteps.Remove(step);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    // ── Dispatch ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="RunbookRun"/> by snapping the current runbook process,
    /// records its target assignment, then enqueues it on the shared task queue for
    /// dispatch by the unified orchestrator.
    /// </summary>
    public async Task<RunbookRun> TriggerAsync(
        Guid runbookId,
        Guid environmentId,
        Guid targetId,
        TaskInitiator initiator,
        CallerAuthorization caller,
        Guid? tenantId = null,
        CancellationToken ct = default)
    {
        // Guard: reject a default/unset initiator before we do any work.
        initiator.EnsureValid();
        ArgumentNullException.ThrowIfNull(caller);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var runbook = await db.Runbooks
            .FirstOrDefaultAsync(r => r.Id == runbookId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Runbook {runbookId} not found.");

        // T1-8: authoritative sub-Space authorization — running THIS runbook's
        // project in THIS environment (+ tenant). Strict; runs for every surface.
        // System-initiated (subscription) triggers skip it (authorized at origin).
        await permissions.EnsureScopedAsync(
            caller, Permission.RunbookRunCreate,
            new PermissionScope(
                SpaceId:       runbook.SpaceId,
                ProjectId:     runbook.ProjectId,
                EnvironmentId: environmentId,
                TenantId:      tenantId),
            ct).ConfigureAwait(false);

        var process = await db.Processes
            .Include(p => p.Steps.OrderBy(s => s.SortOrder))
            .FirstOrDefaultAsync(
                p => p.OwnerKind == ProcessOwnerKind.Runbook && p.OwnerId == runbookId, ct)
            .ConfigureAwait(false);

        if (process is null || process.Steps.Count == 0)
        {
            throw new InvalidOperationException(
                "Runbook has no steps. Add at least one step before triggering a run.");
        }

        var envExists = await db.Environments.AnyAsync(e => e.Id == environmentId, ct).ConfigureAwait(false);
        if (!envExists)
        {
            throw new InvalidOperationException($"Environment {environmentId} not found.");
        }

        var targetExists = await db.DeploymentTargets.AnyAsync(t => t.Id == targetId, ct)
            .ConfigureAwait(false);
        if (!targetExists)
        {
            throw new InvalidOperationException($"Target {targetId} not found.");
        }

        // Snapshot the process (mirrors ReleaseService for releases), including the
        // full execution knobs so the converged orchestrator honours conditions /
        // retries / timeouts / start-triggers for runbook runs too.
        var snapshot = process.Steps
            .OrderBy(s => s.SortOrder)
            .Select(s => new StepSnapshot
            {
                Id                          = s.Id,
                ParentStepId                = s.ParentStepId,
                Name                        = s.Name,
                StepType                    = s.StepType,
                PackageId                   = s.PackageId,
                PackageVersion              = "",   // runbooks don't pin package versions at run time
                TargetRoles                 = [.. s.TargetRoles],
                Config                      = new Dictionary<string, string>(s.Config),
                SortOrder                   = s.SortOrder,
                StepPackageName             = s.StepPackageName,
                StepPackageVersion          = s.StepPackageVersion,
                Condition                   = s.Condition,
                ConditionVariableExpression = s.ConditionVariableExpression,
                Required                    = s.Required,
                MaxRetries                  = s.MaxRetries,
                RetryDelaySeconds           = s.RetryDelaySeconds,
                TimeoutSeconds              = s.TimeoutSeconds,
                StartTrigger                = s.StartTrigger,
            })
            .ToList();

        var run = new RunbookRun
        {
            SpaceId = runbook.SpaceId,
            RunbookId = runbookId,
            ProjectId = runbook.ProjectId,   // denormalized ownership (decision 5)
            EnvironmentId = environmentId,
            TenantId = tenantId,
            Status = DeploymentStatus.Queued,
            ProcessSnapshot = snapshot,
        };
        initiator.StampOnto(run);   // provenance (fix 6)

        db.RunbookRuns.Add(run);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Target set via the assignment join — the single authority, shared with
        // deployments (parity). Single-target today; the join supports fan-out.
        db.TaskTargetAssignments.Add(new TaskTargetAssignment
        {
            TaskId   = run.Id,
            TargetId = targetId,
            AddedUtc = time.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var accountId = accountContext.IsResolved ? accountContext.CurrentAccountId : Guid.Empty;
        await runbookQueue.Writer
            .WriteAsync(new TenantWorkItem(accountId, run.Id), ct)
            .ConfigureAwait(false);

        return run;
    }

    // ── Query runs ─────────────────────────────────────────────────────────────

    public async Task<List<RunbookRun>> GetRunsAsync(Guid runbookId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // RunbookRun is ISpaceScoped (via ServerTask), so db.RunbookRuns is
        // Space-filtered — a cross-Space runbookId simply returns no rows.
        return await db.RunbookRuns
            .Where(r => r.RunbookId == runbookId)
            .Include(r => r.Environment)
            .Include(r => r.Targets).ThenInclude(a => a.Target)
            .OrderByDescending(r => r.CreatedUtc)
            .ToListAsync(ct);
    }

    public async Task<RunbookRun?> GetRunAsync(Guid runId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.RunbookRuns
            .Include(r => r.Runbook)
            .Include(r => r.Environment)
            .Include(r => r.Targets).ThenInclude(a => a.Target)
            .FirstOrDefaultAsync(r => r.Id == runId, ct);
    }

    /// <summary>A runbook run's full log, stitched from compacted step blobs + live
    /// staging in sequence order. Resolves the run first (Space-filtered).</summary>
    public async Task<List<TaskLogLine>> GetRunLogAsync(Guid runId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var exists = await db.RunbookRuns.AnyAsync(r => r.Id == runId, ct).ConfigureAwait(false);
        if (!exists)
        {
            return [];
        }
        return await TaskLogService.ReadAllAsync(db, runId, ct).ConfigureAwait(false);
    }

    /// <summary>All runbook runs across every runbook in the active Space, newest first.</summary>
    public async Task<List<RunbookRun>> GetAllRunsAsync(int? limit = null, CancellationToken ct = default)
    {
        return await GetRunsCoreAsync(targetId: null, limit, ct).ConfigureAwait(false);
    }

    /// <summary>Runbook runs that executed on one target (newest first, bounded).</summary>
    public async Task<List<RunbookRun>> GetRunsForTargetAsync(
        Guid targetId, int limit = 100, CancellationToken ct = default)
    {
        return await GetRunsCoreAsync(targetId, limit, ct).ConfigureAwait(false);
    }

    private async Task<List<RunbookRun>> GetRunsCoreAsync(
        Guid? targetId, int? limit, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        IQueryable<RunbookRun> source = db.RunbookRuns;
        if (targetId is { } tid)
        {
            source = source.Where(r => r.Targets.Any(a => a.TargetId == tid));
        }
        IQueryable<RunbookRun> query = source
            .Include(r => r.Runbook).ThenInclude(rb => rb.Project)
            .Include(r => r.Environment)
            .Include(r => r.Targets).ThenInclude(a => a.Target)
            .Include(r => r.Tenant)
            .OrderByDescending(r => r.CreatedUtc);

        if (limit is > 0)
        {
            query = query.Take(limit.Value);
        }

        return await query.ToListAsync(ct);
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private static async Task<Process> GetOrCreateProcessAsync(
        KrakenDbContext db, Guid runbookId, CancellationToken ct)
    {
        var process = await db.Processes
            .Include(p => p.Steps)
            .FirstOrDefaultAsync(
                p => p.OwnerKind == ProcessOwnerKind.Runbook && p.OwnerId == runbookId, ct)
            .ConfigureAwait(false);

        if (process is not null)
        {
            return process;
        }

        // The Processes query is Space-filtered, so a cross-Space runbookId returns
        // null and would otherwise create a process pointing at an invisible runbook.
        // Validate the runbook is visible in this Space first.
        var runbookExists = await db.Runbooks
            .AnyAsync(r => r.Id == runbookId, ct)
            .ConfigureAwait(false);

        if (!runbookExists)
        {
            throw new InvalidOperationException($"Runbook {runbookId} not found.");
        }

        process = new Process { OwnerKind = ProcessOwnerKind.Runbook, OwnerId = runbookId };
        db.Processes.Add(process);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return process;
    }
}
