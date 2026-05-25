using KrakenDeploy.Server.Core.Domain.Processes;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Manages the deployment process (ordered step list) for a project.
/// </summary>
/// <remarks>
/// <paramref name="stepPackageResolver"/> is optional so tests/fixtures that
/// don't care about D-6 pinning can keep using <c>new ProcessService(db)</c>;
/// when null, auto-pinning of <c>StepPackageVersion</c> is skipped and steps
/// keep whatever the caller passed in (possibly null). In production DI it's
/// always wired.
/// </remarks>
public class ProcessService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    StepPackageResolver? stepPackageResolver = null)
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

    /// <summary>
    /// Appends a new step to the end of the process.
    /// <para>
    /// <paramref name="stepPackageName"/> + <paramref name="stepPackageVersion"/>
    /// (Phase D-6) pin the exact installed step-package the agent will load
    /// to execute this step. Pass both as a unit, or both <c>null</c>:
    /// </para>
    /// <list type="bullet">
    ///   <item>Both null: the service auto-resolves to the highest installed
    ///   semver that claims this step type. When no installed package claims
    ///   it, the pin stays null and the agent falls back to its hardcoded
    ///   handler (bridges the D-6 → D-8 transition).</item>
    ///   <item>Both supplied: the caller has explicitly chosen the pair
    ///   (typically via the D-7 version dropdown).</item>
    /// </list>
    /// </summary>
    public async Task<DeploymentStep> AddStepAsync(
        Guid projectId,
        string name,
        string stepType,
        string packageId,
        List<string> targetRoles,
        Dictionary<string, string> config,
        string? stepPackageName = null,
        string? stepPackageVersion = null,
        StepExecutionKnobs? knobs = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var process = await GetOrCreateCoreAsync(db, projectId, ct).ConfigureAwait(false);

        var maxSort = await db.DeploymentSteps
            .Where(s => s.ProcessId == process.Id)
            .Select(s => (int?)s.SortOrder)
            .MaxAsync(ct)
            .ConfigureAwait(false) ?? -1;

        var pin = await ResolvePinAsync(
                stepType, stepPackageName, stepPackageVersion, ct)
            .ConfigureAwait(false);

        var k = knobs ?? StepExecutionKnobs.Default;
        var step = new DeploymentStep
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
        };

        db.DeploymentSteps.Add(step);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return step;
    }

    /// <summary>
    /// Updates the mutable fields of an existing step. Pass both
    /// <paramref name="stepPackageName"/> and <paramref name="stepPackageVersion"/>
    /// to re-pin (the editor's "switch to version X" path, Phase D-6 / D-7).
    /// Leave both null to keep the existing pin untouched.
    /// </summary>
    public async Task<DeploymentStep?> UpdateStepAsync(
        Guid stepId,
        string name,
        string packageId,
        List<string> targetRoles,
        Dictionary<string, string> config,
        string? stepPackageName = null,
        string? stepPackageVersion = null,
        StepExecutionKnobs? knobs = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var step = await db.DeploymentSteps.FindAsync([stepId], ct).ConfigureAwait(false);
        if (step is null)
        {
            return null;
        }

        step.Name        = name;
        step.PackageId   = packageId;
        step.TargetRoles = targetRoles;
        step.Config      = config;

        if (stepPackageName is not null && stepPackageVersion is not null)
        {
            step.StepPackageName    = stepPackageName;
            step.StepPackageVersion = stepPackageVersion;
        }

        // M14 knobs — null means "leave the row's existing values alone"
        // (an older caller that doesn't know about these knobs MUST NOT
        // accidentally reset Required to false or wipe a configured timeout).
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

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return step;
    }

    /// <summary>
    /// Resolves the pin: explicit (name, version) wins; otherwise asks the
    /// resolver for the highest semver claiming <paramref name="stepType"/>;
    /// returns null when no resolver is wired or no installed package claims
    /// the step type.
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

    // ── Import ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Imports steps from an Octopus <c>deploymentprocess</c> JSON document into
    /// the project's process. The JSON's <c>Properties</c> bag is preserved
    /// verbatim on each created <see cref="DeploymentStep.Config"/> — no Octopus
    /// → Kraken key translation. The runtime step handler decides which shape to
    /// read (dual-shape strategy).
    /// </summary>
    /// <param name="projectId">Target project.</param>
    /// <param name="json">Raw deploymentprocess JSON.</param>
    /// <param name="replace">
    /// When <c>true</c>, existing steps on the project's process are deleted
    /// before the imported steps are appended. When <c>false</c>, imported steps
    /// are appended after existing ones (sort orders shifted accordingly).
    /// </param>
    public async Task<ImportDeploymentProcessResult> ImportDeploymentProcessAsync(
        Guid projectId,
        string json,
        bool replace,
        CancellationToken ct = default)
    {
        var parsed = OctopusDeploymentProcessImporter.Parse(json);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var process = await GetOrCreateCoreAsync(db, projectId, ct).ConfigureAwait(false);

        int replaced = 0;
        if (replace)
        {
            var existing = await db.DeploymentSteps
                .Where(s => s.ProcessId == process.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            replaced = existing.Count;
            db.DeploymentSteps.RemoveRange(existing);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        var startSort = replace
            ? 0
            : (await db.DeploymentSteps
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
    /// Recursively materialises a <see cref="ParsedStep"/> (and its children,
    /// if any) into <see cref="DeploymentStep"/> rows. Children get the
    /// parent's <see cref="DeploymentStep.Id"/> as their
    /// <see cref="DeploymentStep.ParentStepId"/> — EF's
    /// <see cref="Guid.CreateVersion7"/> default on <see cref="Entity"/>
    /// makes this safe before SaveChanges. Returns the total number of
    /// rows added (parent + every descendant).
    /// </summary>
    private async Task<int> AddParsedStepAsync(
        KrakenDbContext db,
        Guid processId,
        Guid? parentStepId,
        int sortOrder,
        ParsedStep p,
        CancellationToken ct)
    {
        // D-6: auto-pin each imported step. When no installed package
        // claims the step type the pin stays null (agent falls back).
        var pin = await ResolvePinAsync(p.StepType, null, null, ct).ConfigureAwait(false);

        var step = new DeploymentStep
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
            // M14 step-execution knobs from the importer.
            Condition                   = p.Condition,
            ConditionVariableExpression = p.ConditionVariableExpression,
            Required                    = p.Required,
            MaxRetries                  = p.MaxRetries,
            RetryDelaySeconds           = p.RetryDelaySeconds,
            TimeoutSeconds              = p.TimeoutSeconds,
            StartTrigger                = p.StartTrigger,
            // M15 parent link
            ParentStepId                = parentStepId,
        };
        db.DeploymentSteps.Add(step);

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
    /// M15 — validates the project's deployment process against the
    /// structural rules in <see cref="ProcessValidator"/>:
    /// cycle freedom, parent locality, group-only parenthood, leaf-config
    /// exclusion on Step Groups. Returns
    /// <see cref="ProcessValidator.Result.Ok"/> for an empty/clean process.
    ///
    /// <para>
    /// Called by the editor before save (the editor surfaces every error
    /// at once) and by the orchestrator's flattener as defence in depth
    /// against corrupted data. Read-only — the validator never mutates
    /// the steps.
    /// </para>
    /// </summary>
    public async Task<ProcessValidator.Result> ValidateAsync(
        Guid projectId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct)
            .ConfigureAwait(false);

        var process = await db.DeploymentProcesses
            .Include(p => p.Steps)
            .FirstOrDefaultAsync(p => p.ProjectId == projectId, ct)
            .ConfigureAwait(false);

        if (process is null)
        {
            return ProcessValidator.Result.Ok;
        }

        return ProcessValidator.Validate(process.Steps);
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

/// <summary>Summary returned by <see cref="ProcessService.ImportDeploymentProcessAsync"/>.</summary>
public sealed record ImportDeploymentProcessResult(
    int Imported,
    int ReplacedExisting,
    IReadOnlyList<ImportDeploymentProcessWarning> Warnings);
