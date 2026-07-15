using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Core.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Manages the deployment process (ordered step list) for a project — rows of the
/// unified <see cref="Process"/> table with <c>OwnerKind = Project</c>.
/// </summary>
/// <remarks>
/// <paramref name="stepPackageResolver"/> is optional so tests/fixtures that don't
/// care about D-6 pinning can keep using <c>new ProcessService(db, permissions)</c>;
/// when null, auto-pinning of <c>StepPackageVersion</c> is skipped.
/// </remarks>
public class ProcessService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    IPermissionEvaluator permissions,
    StepPackageResolver? stepPackageResolver = null)
    : IStepEditingHost
{
    // ── T1-8 authoritative scope check ───────────────────────────────────────
    // Process editing is scoped to the owning project. These resolve the project
    // + its real Space (filter-free, so a foreign-Space id fails closed rather
    // than resolving to null and slipping past) and run the strict check.

    private async Task EnsureProjectScopeAsync(
        KrakenDbContext db, CallerAuthorization caller, Guid projectId, CancellationToken ct)
    {
        if (caller.IsSystem)
        {
            return;
        }
        var spaceId = await db.Projects.IgnoreQueryFilters()
            .Where(p => p.Id == projectId)
            .Select(p => (Guid?)p.SpaceId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        await permissions.EnsureScopedAsync(
            caller, Permission.ProcessEdit,
            new PermissionScope(SpaceId: spaceId, ProjectId: projectId), ct).ConfigureAwait(false);
    }

    // Resolve the owning project from a step (its process owner) so an edit is
    // authorized against the step's REAL project — closing the by-step-id IDOR
    // (route parent id is never trusted for authz).
    private async Task EnsureStepScopeAsync(
        KrakenDbContext db, CallerAuthorization caller, ProcessStep step, CancellationToken ct)
    {
        if (caller.IsSystem)
        {
            return;
        }
        var ownerId = await db.Processes.IgnoreQueryFilters()
            .Where(p => p.Id == step.ProcessId && p.OwnerKind == ProcessOwnerKind.Project)
            .Select(p => (Guid?)p.OwnerId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        await permissions.EnsureScopedAsync(
            caller, Permission.ProcessEdit,
            new PermissionScope(SpaceId: step.SpaceId, ProjectId: ownerId), ct).ConfigureAwait(false);
    }
    // ── IStepEditingHost ───────────────────────────────────────────────
    // Process editor supports the full M14 execution-knobs surface.

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
        var process = await GetProcessByIdAsync(processId, ct).ConfigureAwait(false);
        return process is null
            ? []
            : [.. process.Steps];
    }

    async Task<Guid?> IStepEditingHost.ResolveProjectIdAsync(
        Guid? containerId, Guid? processId, CancellationToken ct)
    {
        // Container id on the process editor IS the project id; bounce it back.
        if (containerId is not null)
        {
            return containerId;
        }
        if (processId is null)
        {
            return null;
        }
        var process = await GetProcessByIdAsync(processId.Value, ct).ConfigureAwait(false);
        // Owner id of a Project-owned process IS the project id.
        return process is { OwnerKind: ProcessOwnerKind.Project } ? process.OwnerId : null;
    }


    // ── Get / create ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns the deployment process for the project, creating an empty one if it
    /// does not exist yet.
    /// </summary>
    public async Task<Process> GetOrCreateAsync(
        Guid projectId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await GetOrCreateCoreAsync(db, projectId, ct).ConfigureAwait(false);
    }

    /// <summary>Returns the process with steps, or null if the project has none.</summary>
    public async Task<Process?> GetAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Processes
            .Include(p => p.Steps.OrderBy(s => s.SortOrder))
            .FirstOrDefaultAsync(
                p => p.OwnerKind == ProcessOwnerKind.Project && p.OwnerId == projectId, ct);
    }

    /// <summary>M15 — Returns the process by its own ID (not the owner's). Used by
    /// the editor to load every step so the "Parent step" dropdown can filter out
    /// the edited step's descendants. Returns null when no process matches.</summary>
    public async Task<Process?> GetProcessByIdAsync(
        Guid processId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Processes
            .Include(p => p.Steps.OrderBy(s => s.SortOrder))
            .FirstOrDefaultAsync(p => p.Id == processId, ct);
    }

    // ── Steps ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Appends a new step to the end of the process. See D-6 pinning semantics on
    /// <paramref name="stepPackageName"/> / <paramref name="stepPackageVersion"/>.
    /// </summary>
    public async Task<ProcessStep> AddStepAsync(
        Guid projectId,
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
        await EnsureProjectScopeAsync(db, caller, projectId, ct).ConfigureAwait(false);
        var process = await GetOrCreateCoreAsync(db, projectId, ct).ConfigureAwait(false);

        // M15 — SortOrder is scoped to siblings (per-parent).
        var maxSort = await db.ProcessSteps
            .Where(s => s.ProcessId == process.Id && s.ParentStepId == parentStepId)
            .Select(s => (int?)s.SortOrder)
            .MaxAsync(ct)
            .ConfigureAwait(false) ?? -1;

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
            SortOrder                   = maxSort + 1,
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

        // M15 — validate the resulting tree in memory BEFORE persisting.
        var existingSteps = await db.ProcessSteps
            .Where(s => s.ProcessId == process.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        EnsureValid([.. existingSteps, step]);

        db.ProcessSteps.Add(step);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return step;
    }

    /// <summary>
    /// Updates the mutable fields of an existing step. Pass both
    /// <paramref name="stepPackageName"/> and <paramref name="stepPackageVersion"/>
    /// to re-pin; leave both null to keep the existing pin untouched.
    /// </summary>
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

        // T1-8: authorize against the step's REAL owning project (IDOR: the route
        // parent id is not trusted). Runs before any mutation.
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

        // M14 knobs — null means "leave existing values alone".
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

        // M15 — parent reassignment; when the parent changes, append to the end of
        // the new sibling group.
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

        // M15 — structural validation as defence in depth (before commit).
        var processSteps = await db.ProcessSteps
            .Where(s => s.ProcessId == step.ProcessId)
            .ToListAsync(ct).ConfigureAwait(false);
        EnsureValid(processSteps);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return step;
    }


    /// <summary>
    /// Resolves the pin: explicit (name, version) wins; otherwise asks the resolver
    /// for the highest semver claiming <paramref name="stepType"/>; returns null
    /// when no resolver is wired or no installed package claims the step type.
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

    /// <summary>
    /// Moves the step one position up or down in the process. <paramref name="direction"/>
    /// is <c>-1</c> for up, <c>+1</c> for down. No-op if already at the edge.
    /// </summary>
    public async Task<bool> MoveStepAsync(
        Guid stepId, int direction, CallerAuthorization caller, CancellationToken ct = default)
    {
        if (direction != -1 && direction != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(direction), "Must be -1 (up) or +1 (down).");
        }
        ArgumentNullException.ThrowIfNull(caller);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var step = await db.ProcessSteps.FindAsync([stepId], ct).ConfigureAwait(false);
        if (step is null)
        {
            return false;
        }

        await EnsureStepScopeAsync(db, caller, step, ct).ConfigureAwait(false);

        // M15 — siblings are scoped to the same ParentStepId.
        var siblings = await db.ProcessSteps
            .Where(s => s.ProcessId == step.ProcessId
                        && s.ParentStepId == step.ParentStepId)
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
    public async Task<bool> RemoveStepAsync(
        Guid stepId, CallerAuthorization caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var step = await db.ProcessSteps
            .Include(s => s.Process)
            .FirstOrDefaultAsync(s => s.Id == stepId, ct)
            .ConfigureAwait(false);

        if (step is null)
        {
            return false;
        }

        await EnsureStepScopeAsync(db, caller, step, ct).ConfigureAwait(false);

        var processId = step.ProcessId;
        db.ProcessSteps.Remove(step);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Re-sequence remaining steps.
        var remaining = await db.ProcessSteps
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

    // ── Import ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Imports steps from an Octopus <c>deploymentprocess</c> JSON document into the
    /// project's process. The JSON's <c>Properties</c> bag is preserved verbatim on
    /// each created <see cref="ProcessStep.Config"/>.
    /// </summary>
    public async Task<ImportDeploymentProcessResult> ImportDeploymentProcessAsync(
        Guid projectId,
        string json,
        bool replace,
        CallerAuthorization caller,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var parsed = OctopusDeploymentProcessImporter.Parse(json);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await EnsureProjectScopeAsync(db, caller, projectId, ct).ConfigureAwait(false);
        var process = await GetOrCreateCoreAsync(db, projectId, ct).ConfigureAwait(false);

        int replaced = 0;
        if (replace)
        {
            var existing = await db.ProcessSteps
                .Where(s => s.ProcessId == process.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            replaced = existing.Count;
            db.ProcessSteps.RemoveRange(existing);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        var startSort = replace
            ? 0
            : (await db.ProcessSteps
                .Where(s => s.ProcessId == process.Id)
                .Select(s => (int?)s.SortOrder)
                .MaxAsync(ct)
                .ConfigureAwait(false) ?? -1) + 1;

        var importedCount = 0;
        for (var i = 0; i < parsed.Steps.Count; i++)
        {
            var p = parsed.Steps[i];
            importedCount += await AddParsedStepAsync(
                db, process.Id, parentStepId: null, sortOrder: startSort + i, p, ct)
                .ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new ImportDeploymentProcessResult(
            Imported: importedCount,
            ReplacedExisting: replaced,
            Warnings: parsed.Warnings);
    }

    /// <summary>
    /// Recursively materialises a <see cref="ParsedStep"/> (and its children) into
    /// <see cref="ProcessStep"/> rows. Returns the total number of rows added.
    /// </summary>
    private async Task<int> AddParsedStepAsync(
        KrakenDbContext db,
        Guid processId,
        Guid? parentStepId,
        int sortOrder,
        ParsedStep p,
        CancellationToken ct)
    {
        var pin = await ResolvePinAsync(p.StepType, null, null, ct).ConfigureAwait(false);

        var step = new ProcessStep
        {
            ProcessId                   = processId,
            Name                        = p.Name,
            StepType                    = p.StepType,
            PackageId                   = p.PackageId,
            TargetRoles                 = p.TargetRoles,
            Config                      = p.Config,
            SortOrder                   = sortOrder,
            StepPackageName             = pin?.Name,
            StepPackageVersion          = pin?.Version,
            Condition                   = p.Condition,
            ConditionVariableExpression = p.ConditionVariableExpression,
            Required                    = p.Required,
            MaxRetries                  = p.MaxRetries,
            RetryDelaySeconds           = p.RetryDelaySeconds,
            TimeoutSeconds              = p.TimeoutSeconds,
            StartTrigger                = p.StartTrigger,
            ParentStepId                = parentStepId,
        };
        db.ProcessSteps.Add(step);

        var added = 1;
        if (p.Children is { Count: > 0 } children)
        {
            for (var ci = 0; ci < children.Count; ci++)
            {
                added += await AddParsedStepAsync(
                    db, processId, parentStepId: step.Id,
                    sortOrder: ci, p: children[ci], ct).ConfigureAwait(false);
            }
        }
        return added;
    }

    // ── M15 validation ─────────────────────────────────────────────────────

    /// <summary>
    /// M15 — validates the project's deployment process against the structural rules
    /// in <see cref="ProcessValidator"/>. Returns
    /// <see cref="ProcessValidator.Result.Ok"/> for an empty/clean process.
    /// </summary>
    public async Task<ProcessValidator.Result> ValidateAsync(
        Guid projectId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct)
            .ConfigureAwait(false);

        var process = await db.Processes
            .Include(p => p.Steps)
            .FirstOrDefaultAsync(
                p => p.OwnerKind == ProcessOwnerKind.Project && p.OwnerId == projectId, ct)
            .ConfigureAwait(false);

        if (process is null)
        {
            return ProcessValidator.Result.Ok;
        }

        return ProcessValidator.Validate(process.Steps);
    }

    // ── Private helpers ────────────────────────────────────────────────────

    // Validates the full step set of a process in memory and throws if any
    // structural invariant is violated. Called BEFORE persisting.
    private static void EnsureValid(IEnumerable<ProcessStep> steps)
    {
        var result = ProcessValidator.Validate(steps);
        if (!result.IsValid)
        {
            throw new ProcessValidationException(result);
        }
    }

    private static async Task<Process> GetOrCreateCoreAsync(
        KrakenDbContext db, Guid projectId, CancellationToken ct)
    {
        var process = await db.Processes
            .Include(p => p.Steps)
            .FirstOrDefaultAsync(
                p => p.OwnerKind == ProcessOwnerKind.Project && p.OwnerId == projectId, ct)
            .ConfigureAwait(false);

        if (process is not null)
        {
            return process;
        }

        // The Processes query above is Space-filtered (Process is ISpaceScoped), so
        // a cross-Space projectId returns null and would otherwise create a process
        // in the CURRENT Space pointing at a Project the caller can't see. Validate
        // the Project is visible in this Space first (db.Projects carries the filter).
        var projectExists = await db.Projects
            .AnyAsync(p => p.Id == projectId, ct)
            .ConfigureAwait(false);

        if (!projectExists)
        {
            throw new InvalidOperationException($"Project {projectId} not found.");
        }

        process = new Process { OwnerKind = ProcessOwnerKind.Project, OwnerId = projectId };
        db.Processes.Add(process);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return process;
    }
}

/// <summary>Summary returned by <see cref="ProcessService.ImportDeploymentProcessAsync"/>.</summary>
public sealed record ImportDeploymentProcessResult(
    int Imported,
    int ReplacedExisting,
    IReadOnlyList<ImportDeploymentProcessWarning> Warnings);

/// <summary>
/// Thrown by <see cref="ProcessService.AddStepAsync"/> / <see cref="ProcessService.UpdateStepAsync"/>
/// when the resulting process state would violate a <see cref="ProcessValidator"/>
/// rule. Validation runs BEFORE the write commits, so no invalid row is persisted.
/// </summary>
public sealed class ProcessValidationException(ProcessValidator.Result result)
    : InvalidOperationException(BuildMessage(result))
{
    public ProcessValidator.Result Result { get; } = result;

    private static string BuildMessage(ProcessValidator.Result r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return r.Errors.Count switch
        {
            0 => "Process validation passed.",
            1 => $"Process validation failed: {r.Errors[0].Message}",
            _ => $"Process validation failed with {r.Errors.Count} error(s): " +
                 string.Join(" | ", r.Errors.Select(e => e.Message)),
        };
    }
}
