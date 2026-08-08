using KrakenDeploy.Contracts.Steps;
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
    /// WP3-b — refuses a manual-intervention step whose configuration would be unusable
    /// at run time, for a caller holding a PROJECT id rather than a Space id (approver
    /// visibility is Space-scoped, so the Space has to be resolved first).
    /// <para>
    /// The rules themselves live in <see cref="ResponsibleTeamResolver"/> and are shared
    /// with the orchestrator's gate, so save-time and run-time cannot drift. The gate
    /// stays the fail-closed backstop; this is only about WHEN the operator finds out,
    /// which used to be "when a deployment failed with somebody waiting on an approval".
    /// </para>
    /// <para>
    /// The Space id is read as <c>Guid?</c> deliberately: a missing project must refuse,
    /// not fall through as <c>Guid.Empty</c>, which matches only system teams and would
    /// report a legitimate Space-scoped approver as unresolvable.
    /// </para>
    /// </summary>
    private static async Task EnsureManualGateConfigForProjectAsync(
        KrakenDbContext db,
        Guid projectId,
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
        var spaceId = await db.Projects.IgnoreQueryFilters()
            .Where(pr => pr.Id == projectId)
            .Select(pr => (Guid?)pr.SpaceId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        await ResponsibleTeamResolver
            .EnsureStepConfigValidAsync(db, spaceId, stepType, name, config, ct)
            .ConfigureAwait(false);
    }

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
        await EnsureManualGateConfigForProjectAsync(
            db, projectId, stepType, name, config, ct).ConfigureAwait(false);
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
            RunOnServer                 = k.RunOnServer,
            MaxParallelism              = k.MaxParallelism,
            ForEachCollection           = k.ForEachCollection,
            ForEachParallel             = k.ForEachParallel,
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

        // M14 knobs + D3 control-flow flags — null means "leave existing values alone".
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

        // WP3-b — report unusable manual-intervention gates as WARNINGS rather than
        // refusing the import. This path is deliberately not the throwing guard the
        // editor uses: an Octopus process ALWAYS carries Octopus team ids
        // ("teams-123"), which never resolve to Kraken teams, so throwing would make
        // importing any process containing an approval step impossible — the exact
        // workflow the importer exists for. The orchestrator's gate remains the
        // fail-closed backstop; what was missing was any signal at all at import time,
        // so the operator only found out when a deployment hard-failed with somebody
        // waiting on an approval.
        var warnings = new List<ImportDeploymentProcessWarning>(parsed.Warnings);
        var spaceId = await db.Projects.IgnoreQueryFilters()
            .Where(pr => pr.Id == projectId)
            .Select(pr => (Guid?)pr.SpaceId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        foreach (var gate in FlattenParsed(parsed.Steps)
                     .Where(p => ResponsibleTeamResolver.IsGateStep(p.StepType)))
        {
            var error = spaceId is { } sid && sid != Guid.Empty
                ? await ResponsibleTeamResolver
                    .ValidateStepConfigAsync(db, sid, gate.StepType, gate.Name, gate.Config, ct)
                    .ConfigureAwait(false)
                : $"Manual intervention step '{gate.Name}' could not be validated: the " +
                  "project's Space could not be resolved.";
            if (error is not null)
            {
                warnings.Add(new ImportDeploymentProcessWarning(gate.Name, error));
            }
        }

        return new ImportDeploymentProcessResult(
            Imported: importedCount,
            ReplacedExisting: replaced,
            Warnings: warnings);
    }

    /// <summary>Depth-first flatten of a parsed step tree, so a gate nested inside a
    /// rolling/ForEach parent is validated too.</summary>
    private static IEnumerable<ParsedStep> FlattenParsed(IEnumerable<ParsedStep> steps)
    {
        foreach (var s in steps)
        {
            yield return s;
            if (s.Children is { Count: > 0 } kids)
            {
                foreach (var child in FlattenParsed(kids))
                {
                    yield return child;
                }
            }
        }
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
            RunOnServer                 = p.RunOnServer,
            MaxParallelism              = p.MaxParallelism,
            ForEachCollection           = p.ForEachCollection,
            ForEachParallel             = p.ForEachParallel,
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

        var result = ProcessValidator.Validate(process.Steps);
        var warnings = await GatedChildWarningsAsync(db, process.Steps, ct)
            .ConfigureAwait(false);
        return warnings.Count == 0 ? result : result with { Warnings = warnings };
    }

    /// <summary>
    /// WP3-b — advisory note for each <c>Octopus.DeployRelease</c> step whose CHILD
    /// project's process contains a manual-intervention gate.
    /// <para>
    /// The parent step waits on the child, so such a composite deployment will sit until a
    /// human answers the child's gate. That is now correct behaviour —
    /// <c>WaitForChildAsync</c> charges only non-paused time against
    /// <c>Engine:MaxDeployReleaseWaitDuration</c>, so the parent no longer fails at one
    /// hour against a 72 h approval window — but it is still surprising enough to say out
    /// loud: the operator who queues the parent is not necessarily the person who can
    /// approve the child, and nothing else on the parent's process hints that it depends
    /// on somebody's decision.
    /// </para>
    /// <para>
    /// A WARNING, not an error: the combination is legitimate.
    /// </para>
    /// </summary>
    private static async Task<List<string>> GatedChildWarningsAsync(
        KrakenDbContext db, IEnumerable<ProcessStep> steps, CancellationToken ct)
    {
        var warnings = new List<string>();
        foreach (var step in steps.Where(s => s.StepType.Equals(
                     DeployReleaseConfigKeys.StepType, StringComparison.OrdinalIgnoreCase)))
        {
            var warning = await GatedChildWarningAsync(db, step.Name, step.Config, ct)
                .ConfigureAwait(false);
            if (warning is not null)
            {
                warnings.Add(warning);
            }
        }
        return warnings;
    }

    /// <summary>
    /// The advisory for ONE <c>Octopus.DeployRelease</c> step, or <c>null</c> when its
    /// child project has no gate (and when the config names no resolvable project — the
    /// runner also accepts a slug or name, and re-implementing that resolution here would
    /// be a second source of truth for it).
    /// <para>
    /// Public so the step editor can show it immediately after a save: <c>ValidateAsync</c>
    /// is the aggregate view, but nothing in the product calls it yet, and an advisory
    /// nobody sees is not worth computing.
    /// </para>
    /// </summary>
    public async Task<string?> DescribeGatedChildAsync(
        string stepName,
        string stepType,
        IReadOnlyDictionary<string, string> config,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!stepType.Equals(DeployReleaseConfigKeys.StepType, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await GatedChildWarningAsync(db, stepName, config, ct).ConfigureAwait(false);
    }

    private static async Task<string?> GatedChildWarningAsync(
        KrakenDbContext db,
        string stepName,
        IReadOnlyDictionary<string, string> config,
        CancellationToken ct)
    {
        var raw = ManualInterventionConfigKeys.Read(config, DeployReleaseConfigKeys.ProjectId);
        if (!Guid.TryParse(raw, out var childProjectId))
        {
            return null;
        }

        var gateNames = await db.Processes
            .IgnoreQueryFilters()
            .Where(p => p.OwnerKind == ProcessOwnerKind.Project && p.OwnerId == childProjectId)
            .SelectMany(p => p.Steps)
            // ILike keeps the comparison case-insensitive IN POSTGRES — a client-side
            // OrdinalIgnoreCase would pull the whole step set into memory, and StepType is
            // stored verbatim as the caller supplied it.
            .Where(s => EF.Functions.ILike(s.StepType, ManualInterventionConfigKeys.StepType))
            .Select(s => s.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (gateNames.Count == 0)
        {
            return null;
        }

        var childName = await db.Projects.IgnoreQueryFilters()
            .Where(p => p.Id == childProjectId)
            .Select(p => p.Name)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false) ?? childProjectId.ToString();

        return $"Step '{stepName}' deploys project '{childName}', whose process contains " +
               $"manual-intervention gate(s): {string.Join(", ", gateNames)}. This deployment " +
               "will PAUSE until somebody approves the child. The wait no longer counts " +
               "against this step's timeout, but whoever runs the parent needs to know an " +
               "approval is required.";
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
