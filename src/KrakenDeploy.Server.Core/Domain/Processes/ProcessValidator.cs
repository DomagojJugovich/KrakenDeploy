namespace KrakenDeploy.Server.Core.Domain.Processes;

/// <summary>
/// M15 — pure-function validator for the tree-shaped deployment process
/// the M15 schema introduces. The validator enforces the structural
/// invariants the flattener + orchestrator rely on:
/// <list type="number">
///   <item>Cycle freedom — a step cannot be its own ancestor.</item>
///   <item>Parent locality — <c>ParentStepId</c> must reference a step
///         in the same process.</item>
///   <item>Group-only parenthood — only
///         <see cref="KrakenStepTypes.StepGroup"/>-typed steps may have
///         children; leaf step types (<c>Kraken.Script</c>, <c>Kraken.IIS</c>,
///         …) cannot.</item>
///   <item>Leaf-config exclusion — a <see cref="KrakenStepTypes.StepGroup"/>
///         step must NOT carry leaf-only Config keys (script body, package
///         selectors, etc.). See
///         <see cref="KrakenStepTypes.LeafOnlyConfigKeys"/>.</item>
/// </list>
///
/// <para>
/// Called from <c>ProcessService.ValidateAsync</c> at design time (the
/// editor refuses an invalid save) AND from the orchestrator's flattener
/// as defence in depth (corrupted data must fail the deployment with a
/// clear message rather than throw mid-walk). Pure-function with no
/// EF dependency so unit tests can pass in synthetic step lists.
/// </para>
/// </summary>
public static class ProcessValidator
{
    /// <summary>
    /// One validation error. <see cref="StepId"/> is the offending step
    /// (the child for cycle / locality errors, the parent for group-only
    /// errors). <see cref="Code"/> is a machine-readable category so
    /// callers (UI, importer warnings, log lines) can switch on it.
    /// </summary>
    public sealed record ValidationError(Guid StepId, ValidationErrorCode Code, string Message);

    public sealed record Result(IReadOnlyList<ValidationError> Errors)
    {
        public bool IsValid => Errors.Count == 0;

        public static Result Ok { get; } = new([]);
    }

    /// <summary>
    /// Categorical reasons a step list can fail validation. Stays an enum
    /// (not magic strings) so the UI can map each one to a localised
    /// message + a deep link to the offending step.
    /// </summary>
    public enum ValidationErrorCode
    {
        /// <summary>A step's <c>ParentStepId</c> chain reaches itself.</summary>
        Cycle,

        /// <summary>A step's <c>ParentStepId</c> references a Guid that
        /// isn't in the provided step list (different process, deleted
        /// step, or just garbage).</summary>
        UnknownParent,

        /// <summary>A non-<see cref="KrakenStepTypes.StepGroup"/> step
        /// has children. Leaf types can't be parents.</summary>
        LeafTypeHasChildren,

        /// <summary>A <see cref="KrakenStepTypes.StepGroup"/> step carries
        /// a Config key from <see cref="KrakenStepTypes.LeafOnlyConfigKeys"/>.
        /// Step Groups must not carry leaf semantics — script body,
        /// package selectors, etc.</summary>
        GroupHasLeafConfig,
    }

    /// <summary>
    /// Validates a snapshot of one process's steps. Callers pass the
    /// in-memory list (typically just-loaded from <c>DeploymentSteps
    /// WHERE ProcessId = X</c>) — the validator does its own indexing.
    /// Errors are accumulated; the validator does NOT short-circuit on
    /// the first error so the editor can surface all problems at once.
    /// </summary>
    public static Result Validate(IEnumerable<DeploymentStep> stepsEnumerable)
    {
        ArgumentNullException.ThrowIfNull(stepsEnumerable);

        // Materialise once — the validator does two passes (per-step
        // checks + cycle DFS) and the typical input is already a list.
        var steps = stepsEnumerable as IReadOnlyCollection<DeploymentStep>
                    ?? [.. stepsEnumerable];
        if (steps.Count == 0)
        {
            return Result.Ok;
        }

        var byId = steps.ToDictionary(s => s.Id);
        var childrenByParent = steps
            .Where(s => s.ParentStepId is not null)
            .GroupBy(s => s.ParentStepId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var errors = new List<ValidationError>();

        foreach (var step in steps)
        {
            // ── Group-only parenthood ───────────────────────────────────
            if (childrenByParent.TryGetValue(step.Id, out var kids)
                && kids.Count > 0
                && !string.Equals(step.StepType, KrakenStepTypes.StepGroup,
                                  StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new ValidationError(
                    step.Id, ValidationErrorCode.LeafTypeHasChildren,
                    $"Step '{step.Name}' has type '{step.StepType}' but " +
                    $"carries {kids.Count} child step(s). Only " +
                    $"'{KrakenStepTypes.StepGroup}' steps may have children."));
            }

            // ── Leaf-config exclusion on Step Groups ────────────────────
            if (string.Equals(step.StepType, KrakenStepTypes.StepGroup,
                              StringComparison.OrdinalIgnoreCase)
                && KrakenStepTypes.HasLeafOnlyConfigKey(step.Config))
            {
                var offending = step.Config.Keys
                    .Where(k => KrakenStepTypes.LeafOnlyConfigKeys.Contains(k))
                    .OrderBy(k => k, StringComparer.Ordinal)
                    .ToList();
                errors.Add(new ValidationError(
                    step.Id, ValidationErrorCode.GroupHasLeafConfig,
                    $"Step Group '{step.Name}' carries leaf-only Config key(s): " +
                    $"[{string.Join(", ", offending)}]. Step Groups have no " +
                    $"script body or package — move these properties onto a " +
                    $"child step."));
            }

            // ── Parent locality ─────────────────────────────────────────
            if (step.ParentStepId is { } parentId
                && !byId.ContainsKey(parentId))
            {
                errors.Add(new ValidationError(
                    step.Id, ValidationErrorCode.UnknownParent,
                    $"Step '{step.Name}' references parent {parentId} but no " +
                    $"step with that ID exists in the process."));
            }
        }

        // ── Cycle detection ────────────────────────────────────────────
        // DFS over the parent chain. Each step's chain is walked at most
        // once thanks to `seen`; a cycle shows up as the walk re-visiting
        // a step that's currently on the stack.
        var seen = new HashSet<Guid>();
        foreach (var step in steps)
        {
            if (seen.Contains(step.Id))
            {
                continue;
            }
            var path = new HashSet<Guid>();
            var current = step;
            while (current is not null)
            {
                if (!path.Add(current.Id))
                {
                    errors.Add(new ValidationError(
                        step.Id, ValidationErrorCode.Cycle,
                        $"Step '{step.Name}' is part of a parent cycle that " +
                        $"reaches step {current.Id}."));
                    break;
                }
                seen.Add(current.Id);
                if (current.ParentStepId is { } pid
                    && byId.TryGetValue(pid, out var parent))
                {
                    current = parent;
                }
                else
                {
                    current = null;
                }
            }
        }

        return new Result(errors);
    }
}
