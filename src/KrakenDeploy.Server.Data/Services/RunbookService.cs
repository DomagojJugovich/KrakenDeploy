using System.Threading.Channels;
using KrakenDeploy.Server.Core.Domain.Accounts;
using KrakenDeploy.Server.Core.Domain.Audit;
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
        DateTimeOffset? scheduledFor = null,
        IReadOnlyCollection<Guid>? additionalTargetIds = null,
        DeploymentFailureMode failureMode = DeploymentFailureMode.BestEffort,
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
    // D1 engine merge: runbook runs enqueue onto the SHARED task channel the
    // unified orchestrator (DeploymentWorker) reads — the degraded
    // RunbookRunWorker + RunbookRunChannel are gone. The worker branches on
    // ServerTask.Kind when it dequeues.
    Channel<TenantWorkItem> taskQueue,
    TimeProvider time,
    IAccountContext accountContext,
    IPermissionEvaluator permissions,
    StepPackageResolver? stepPackageResolver = null,
    // B6: optional — registered in the server host; tests that construct the
    // service directly skip the agent push.
    IAgentCancelPusher? cancelPusher = null,
    // Optional (same host-registered / tests-skip pattern as cancelPusher):
    // CancelRunAsync records the semantic RunbookRun.Cancelled audit itself so no
    // cancel surface can omit it. Null in tests → no semantic row (unasserted).
    IAuditLog? auditLog = null)
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

    /// <summary>
    /// WP3-b — refuses a manual-intervention step whose configuration would be unusable at
    /// run time. Runbook runs pause at gates exactly as deployments do, but this guard
    /// existed only on <c>ProcessService</c>, so the SAME step editor refused a bad gate on
    /// a project process and silently accepted it on a runbook — which then hard-failed
    /// when the run reached the gate, with somebody waiting on an approval.
    /// <para>
    /// Rules live in <see cref="ResponsibleTeamResolver"/> and are shared with the
    /// orchestrator's gate, so save-time and run-time cannot drift. The Space is read as
    /// <c>Guid?</c> so a missing runbook refuses rather than falling through as
    /// <c>Guid.Empty</c>, which matches only system teams.
    /// </para>
    /// </summary>
    private static async Task EnsureManualGateConfigAsync(
        KrakenDbContext db,
        Guid runbookId,
        string stepType,
        string name,
        IReadOnlyDictionary<string, string> config,
        CancellationToken ct)
    {
        // Skip the extra read entirely for the overwhelming majority of steps.
        if (!ResponsibleTeamResolver.IsGateStep(stepType))
        {
            return;
        }
        var spaceId = await db.Runbooks.IgnoreQueryFilters()
            .Where(r => r.Id == runbookId)
            .Select(r => (Guid?)r.SpaceId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        await ResponsibleTeamResolver
            .EnsureStepConfigValidAsync(db, spaceId, stepType, name, config, ct)
            .ConfigureAwait(false);
    }

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
        Guid projectId, string name, string? description,
        CallerAuthorization caller, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(caller);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // T1-8: creating a runbook under THIS project (RunbookEdit). Strict;
        // resolve the project's Space filter-free so a foreign-Space project id
        // fails closed. Mirrors ReleaseService.CreateAsync; System callers skip.
        if (!caller.IsSystem)
        {
            var spaceId = await db.Projects.IgnoreQueryFilters()
                .Where(p => p.Id == projectId)
                .Select(p => (Guid?)p.SpaceId)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            await permissions.EnsureScopedAsync(
                caller, Permission.RunbookEdit,
                new PermissionScope(SpaceId: spaceId, ProjectId: projectId), ct)
                .ConfigureAwait(false);
        }

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
        Guid id, string name, string? description,
        CallerAuthorization caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // T1-8: renaming/editing THIS runbook is scoped to its owning project
        // (RunbookEdit), strict + filter-free. Checked before any read so an
        // unauthorized caller can't distinguish "not found" from "forbidden".
        await EnsureRunbookScopeAsync(db, caller, id, ct).ConfigureAwait(false);

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

    /// <summary>
    /// WP9 — sets the per-runbook retention override for how many successful runs
    /// are kept per (runbook, environment). <paramref name="keepRuns"/> is a true
    /// tri-state: <c>null</c> clears the override (inherit the instance-wide
    /// <c>PerformanceSettings.RunbookRunRetentionKeep</c>), <c>0</c> keeps all runs,
    /// a positive value keeps that many. Kept separate from
    /// <see cref="UpdateAsync"/> so the name/description REST path (which carries no
    /// retention field) never accidentally clears an operator's override. Authorized
    /// like any other runbook edit (RunbookEdit on the owning project).
    /// </summary>
    public async Task<Runbook?> SetRetentionOverrideAsync(
        Guid id, int? keepRuns, CallerAuthorization caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await EnsureRunbookScopeAsync(db, caller, id, ct).ConfigureAwait(false);

        var runbook = await db.Runbooks.FindAsync([id], ct).ConfigureAwait(false);
        if (runbook is null)
        {
            return null;
        }

        runbook.RetentionKeepRuns = keepRuns;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return runbook;
    }

    /// <summary>
    /// F6 — sets the runbook's "allow parallel task execution" consent (see
    /// <see cref="Runbook.AllowParallelTaskExecution"/>). Kept separate from
    /// <see cref="UpdateAsync"/> for the same reason as the retention override:
    /// a name/description edit path that carries no concurrency field must never
    /// silently clear an author's consent. Applies to the NEXT claim/dispatch —
    /// work already queued or in flight keeps the mode it was claimed with.
    /// </summary>
    public async Task<Runbook?> SetAllowParallelTaskExecutionAsync(
        Guid id, bool allow, CallerAuthorization caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await EnsureRunbookScopeAsync(db, caller, id, ct).ConfigureAwait(false);

        var runbook = await db.Runbooks.FindAsync([id], ct).ConfigureAwait(false);
        if (runbook is null)
        {
            return null;
        }

        runbook.AllowParallelTaskExecution = allow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return runbook;
    }

    /// <summary>
    /// F6 read-side helper — the target-wait reason for a still-<c>Queued</c>
    /// run, or <c>null</c> when no serial target is contended. The UI read of the
    /// SAME query the claim refuses on (<see cref="ServerTaskTargetExclusion"/>),
    /// so the RunbookRunDetail banner can never drift from the actual gate; the
    /// runbook analogue of <c>DeploymentService.GetTargetConflictAsync</c>.
    /// </summary>
    public async Task<ServerTaskTargetExclusion.TargetConflict?> GetTargetConflictAsync(
        Guid taskId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await ServerTaskTargetExclusion
            .DescribeConflictAsync(db, taskId, time.GetUtcNow(), ct)
            .ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(Guid id, CallerAuthorization caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // T1-8: deleting THIS runbook (+ its process + steps) is scoped to its
        // owning project (RunbookEdit), strict + filter-free — a destructive
        // cross-project op must not pass on a Space-wide grant.
        await EnsureRunbookScopeAsync(db, caller, id, ct).ConfigureAwait(false);

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

    /// <summary>
    /// D3 — reads a single runbook step (Space/permission-scoped via
    /// <see cref="EnsureStepScopeAsync"/>) so the REST update endpoint can merge
    /// the typed control-flow flags onto the step's existing execution knobs
    /// without resetting the M14 knobs the REST contract does not carry. Returns
    /// <c>null</c> when the step does not exist; throws on an unauthorized caller.
    /// </summary>
    public async Task<ProcessStep?> GetStepAsync(
        Guid stepId, CallerAuthorization caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var step = await db.ProcessSteps.FindAsync([stepId], ct).ConfigureAwait(false);
        if (step is null)
        {
            return null;
        }
        await EnsureStepScopeAsync(db, caller, step, ct).ConfigureAwait(false);
        return step;
    }

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
        await EnsureManualGateConfigAsync(db, runbookId, stepType, name, config, ct)
            .ConfigureAwait(false);
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
            RunOnServer                 = k.RunOnServer,
            MaxParallelism              = k.MaxParallelism,
            ForEachCollection           = k.ForEachCollection,
            ForEachParallel             = k.ForEachParallel,
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
        // The step carries its own SpaceId, so no runbook lookup is needed here.
        await ResponsibleTeamResolver.EnsureStepConfigValidAsync(
            db, step.SpaceId, step.StepType, name, config, ct).ConfigureAwait(false);

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
            step.RunOnServer                 = knobs.RunOnServer;
            step.MaxParallelism              = knobs.MaxParallelism;
            step.ForEachCollection           = knobs.ForEachCollection;
            step.ForEachParallel             = knobs.ForEachParallel;
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
    /// records its target assignments, then enqueues it on the shared task queue for
    /// dispatch by the unified orchestrator.
    /// <para>
    /// D1 Phase 2 — the trigger surface mirrors
    /// <see cref="DeploymentService.CreateAsync"/>: the target set is the union of
    /// <paramref name="targetId"/> (the primary — always first) and
    /// <paramref name="additionalTargetIds"/>, persisted exclusively as assignment
    /// rows; a FUTURE <paramref name="scheduledFor"/> holds the run <c>Queued</c>
    /// for the scheduled-dispatch job (a due/past value is normalized to null and
    /// dispatched immediately — exactly one dispatch path per run); and
    /// <paramref name="failureMode"/> picks how the rolling orchestrator reacts
    /// when a target fails a Required step (BestEffort drops the target,
    /// Atomic fails the whole run).
    /// </para>
    /// </summary>
    public async Task<RunbookRun> TriggerAsync(
        Guid runbookId,
        Guid environmentId,
        Guid targetId,
        TaskInitiator initiator,
        CallerAuthorization caller,
        Guid? tenantId = null,
        DateTimeOffset? scheduledFor = null,
        IReadOnlyCollection<Guid>? additionalTargetIds = null,
        DeploymentFailureMode failureMode = DeploymentFailureMode.BestEffort,
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

        if (tenantId.HasValue)
        {
            var tenantExists = await db.Tenants.AnyAsync(t => t.Id == tenantId.Value, ct)
                .ConfigureAwait(false);
            if (!tenantExists)
            {
                // Fail fast with a clean message (parity with CreateAsync) instead
                // of letting the insert hit fk_server_tasks_tenants_tenant_id and
                // surface as an uncaught DbUpdateException -> HTTP 500.
                throw new InvalidOperationException($"Tenant {tenantId.Value} not found.");
            }
        }

        // ── Build the target id set (mirrors DeploymentService.CreateAsync) ──
        // Primary targetId is always part of the set (the first assignment row —
        // server waves resolve machine variables against it). Additional ids
        // extend it; duplicates are de-duplicated.
        var targetIds = new List<Guid> { targetId };
        if (additionalTargetIds is not null)
        {
            foreach (var id in additionalTargetIds)
            {
                if (id != targetId && !targetIds.Contains(id))
                {
                    targetIds.Add(id);
                }
            }
        }
        // Validate every target id exists BEFORE inserting the run, so a bogus or
        // cross-Space id fails fast here with a clear message instead of opaquely
        // at dispatch.
        var existing = await db.DeploymentTargets
            .Where(t => targetIds.Contains(t.Id))
            .Select(t => t.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        var missing = targetIds.Where(id => !existing.Contains(id)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Target(s) not found: {string.Join(", ", missing)}.");
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
                // D3 — freeze the control-flow flags too (parity with ReleaseService).
                RunOnServer                 = s.RunOnServer,
                MaxParallelism              = s.MaxParallelism,
                ForEachCollection           = s.ForEachCollection,
                ForEachParallel             = s.ForEachParallel,
            })
            .ToList();

        // B1/T1-2 (parity with CreateAsync): exactly ONE dispatch path per run.
        // Only a genuinely FUTURE instant is persisted (the scheduled job is then
        // the sole dispatcher); a due/past value dispatches immediately below.
        // Normalize to UTC: Npgsql rejects a DateTimeOffset with a non-zero offset
        // on a timestamptz column (the UI picker + REST both hand us a local
        // offset), so persisting scheduledFor verbatim would throw at SaveChanges.
        var scheduledUtc = scheduledFor?.ToUniversalTime();
        var isScheduledForFuture = scheduledUtc.HasValue &&
            scheduledUtc.Value > time.GetUtcNow();

        var run = new RunbookRun
        {
            SpaceId = runbook.SpaceId,
            RunbookId = runbookId,
            ProjectId = runbook.ProjectId,   // denormalized ownership (decision 5)
            EnvironmentId = environmentId,
            TenantId = tenantId,
            Status = DeploymentStatus.Queued,
            FailureMode = failureMode,
            ScheduledFor = isScheduledForFuture ? scheduledUtc : null,
            ProcessSnapshot = snapshot,
        };
        initiator.StampOnto(run);   // provenance (fix 6)

        // Target set via the assignment join — the single authority, shared with
        // deployments (parity). AddedUtc gets a strictly increasing MICROSECOND
        // per row so assignment ORDER survives the DB round-trip and the
        // first-assigned target stays canonical (machine-variable resolution
        // for server waves). Added to the SAME change set as the run so run +
        // assignments commit atomically — a crash between two saves would else
        // leave a Queued run with no targets that the reconciler re-signals into
        // an empty-target-set dispatch.
        var now = time.GetUtcNow();
        for (var i = 0; i < targetIds.Count; i++)
        {
            db.TaskTargetAssignments.Add(new TaskTargetAssignment
            {
                TaskId   = run.Id,
                TargetId = targetIds[i],
                AddedUtc = now.AddMicroseconds(i),
            });
        }
        db.RunbookRuns.Add(run);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Dispatch immediately unless the caller requested a future start time.
        if (!isScheduledForFuture)
        {
            var accountId = accountContext.IsResolved ? accountContext.CurrentAccountId : Guid.Empty;
            await taskQueue.Writer
                .WriteAsync(new TenantWorkItem(accountId, run.Id), ct)
                .ConfigureAwait(false);
        }

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
            .Include(r => r.Runbook).ThenInclude(rb => rb.Project)
            .Include(r => r.Environment)
            .Include(r => r.Targets).ThenInclude(a => a.Target)
            .FirstOrDefaultAsync(r => r.Id == runId, ct);
    }

    /// <summary>
    /// B6 — transitions a non-terminal runbook run to <c>Cancelled</c> (B5
    /// guarded write) and best-effort pushes the abort to the executing
    /// agent(s). A <c>Queued</c> run is never claimed afterwards (the B1
    /// conditional claim skips non-Queued rows); for a <c>Running</c> run the
    /// orchestrator observes the flip at the next wave boundary while the push
    /// kills the in-flight step's process tree within seconds — a killed
    /// attempt's late completion is swallowed by the terminal guard. Returns
    /// <c>null</c> when the run does not exist (or is outside the active
    /// Space); throws <see cref="InvalidOperationException"/> when it is
    /// already terminal.
    /// </summary>
    public async Task<RunbookRun?> CancelRunAsync(
        Guid id, CallerAuthorization caller, CancellationToken ct = default)
    {
        // D1 Phase 2 — shared cancel core (T1-8 scope probe → B5 guarded flip →
        // B6 abort push), see ServerTaskCanceller.
        var run = await ServerTaskCanceller.CancelAsync<RunbookRun>(
            dbFactory, permissions, time, cancelPusher, id, caller,
            taskNoun: "Runbook run",
            pushReason: "Runbook run cancelled by operator.",
            ct).ConfigureAwait(false);

        // Record the SEMANTIC cancel event here, not at each call site, so no
        // cancel surface can omit it (a non-null return means the guarded flip
        // won). The subscription poller prefix-matches "RunbookRun." — a silent
        // cancel would drop the operator's notification.
        if (run is not null && auditLog is not null)
        {
            await auditLog.RecordAsync(
                AuditEventType.RunbookRunCancelled,
                subjectType: "RunbookRun",
                subjectId:   id.ToString(),
                details:     "Runbook run cancelled.",
                ct:          ct).ConfigureAwait(false);
        }
        return run;
    }

    /// <summary>Whether a runbook run with this id exists in the active Space.
    /// The kind gate for the read endpoints — a deployment id (or a run in
    /// another Space) returns <c>false</c>, so those endpoints 404 rather than
    /// serving a deployment's children under a runbook-run route.</summary>
    public async Task<bool> RunExistsAsync(Guid runId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.RunbookRuns.AnyAsync(r => r.Id == runId, ct).ConfigureAwait(false);
    }

    /// <summary>A runbook run's log in sequence order, stitched from compacted step
    /// blobs + live staging. Resolves the run first (Space-filtered). Pass
    /// <paramref name="afterSequence"/> to tail incrementally (only lines with a
    /// higher sequence); the default (-1) returns the full log.</summary>
    public async Task<List<TaskLogLine>> GetRunLogAsync(
        Guid runId, int afterSequence = -1, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var exists = await db.RunbookRuns.AnyAsync(r => r.Id == runId, ct).ConfigureAwait(false);
        if (!exists)
        {
            return [];
        }
        return await TaskLogService.ReadSinceAsync(db, runId, afterSequence, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// D1 Phase 2 — the terminal per-step outcomes of a runbook run, ordered by
    /// step index (the run analogue of
    /// <see cref="DeploymentService.GetStepOutcomesAsync"/>). Resolves the run
    /// first (Space-filtered) so a deployment id or a foreign run returns empty.
    /// </summary>
    public async Task<List<TaskStepOutcome>> GetRunStepOutcomesAsync(
        Guid runId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var exists = await db.RunbookRuns.AnyAsync(r => r.Id == runId, ct).ConfigureAwait(false);
        if (!exists)
        {
            return [];
        }
        return await db.TaskStepOutcomes
            .Where(o => o.TaskId == runId)
            .OrderBy(o => o.StepIndex)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// D1 Phase 2 — output variables captured during a runbook run via
    /// <c>Set-OctopusVariable</c> / <c>##octopus[setVariable]</c> markers (the run
    /// analogue of <see cref="DeploymentService.GetOutputVariablesAsync"/>).
    /// T0-6: sensitive rows are masked to <c>***</c> at this boundary — the
    /// ciphertext is never returned. Resolves the run first (Space-filtered).
    /// </summary>
    public async Task<List<TaskOutputVariable>> GetRunOutputVariablesAsync(
        Guid runId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var exists = await db.RunbookRuns.AnyAsync(r => r.Id == runId, ct).ConfigureAwait(false);
        if (!exists)
        {
            return [];
        }
        var rows = await db.TaskOutputVariables
            .Where(o => o.TaskId == runId)
            .OrderBy(o => o.CapturedUtc)
            .ThenBy(o => o.Name)
            .ToListAsync(ct).ConfigureAwait(false);

        // Mask at the boundary (rows are detached — mutation is not persisted).
        foreach (var row in rows)
        {
            if (row.IsSensitive)
            {
                row.Value = "***";
            }
        }
        return rows;
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
