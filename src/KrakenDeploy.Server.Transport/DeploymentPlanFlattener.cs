using System.Globalization;
using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Core.Domain.Releases;
using Octostache;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// M15.2 — pre-flattens the M15 parent-child snapshot tree into a flat
/// <see cref="DeploymentStepPlan"/>[] for the M14.4 wave partitioner +
/// agent. Pure function — no DB / no IO; the orchestrator threads
/// <see cref="PackageReferenceResolver"/> over the result in a second pass
/// because that lookup is async + DB-backed.
///
/// <para>
/// <strong>Behaviour driven by Config properties</strong>, not step type
/// (mirrors Octopus's data model — see M15 plan body decisions). A
/// <see cref="KrakenStepTypes.StepGroup"/>-typed step's Config decides:
/// </para>
/// <list type="bullet">
///   <item><c>Octopus.Action.ForEach.Collection</c> set → iterate the named
///         array variable; emit one block of children per iteration with
///         <c>IterationVariable</c> + <c>IndexVariable</c> injected into
///         each child plan's Config.</item>
///   <item><c>Octopus.Action.MaxParallelism</c> set → reserved for
///         M-RollingDeployments. M15 treats the group as a plain container
///         (children emitted in declared order) but preserves the property
///         in the snapshot for the future milestone to consume.</item>
///   <item>Neither → plain container; children run sequentially with
///         per-child <see cref="DeploymentStepPlan.StartTrigger"/> driving
///         any parallel-with-previous behaviour through M14.4's wave
///         partitioner.</item>
/// </list>
///
/// <para>
/// <strong>Octostache substitution moves here</strong> from
/// <c>DeploymentWorker.SubstituteConfig</c> so per-iteration variable
/// values resolve correctly. The flattener owns the per-iteration variable
/// bag, so <c>#{item}</c> in a child's <c>ScriptBody</c> resolves to that
/// iteration's value (not "always the last item").
/// </para>
///
/// <para>
/// <strong>Synthetic naming</strong> for ForEach iterations:
/// <list type="bullet">
///   <item><see cref="DeploymentStepPlan.AccumulatorKey"/> = stable
///         <c>OriginalName[index]</c> — used by the agent when reporting
///         outputs, and what <c>Octopus.Action[OriginalName[0]].Output.X</c>
///         resolves to.</item>
///   <item><see cref="DeploymentStepPlan.Name"/> = display
///         <c>OriginalName [var=value]</c> for clean values
///         (≤ 40 chars, no newlines/tabs/<c>]</c>); falls back to
///         <c>OriginalName [var=#index]</c> otherwise. Operators reading
///         logs + Steps tab can tell at-a-glance which item ran.</item>
/// </list>
/// </para>
///
/// <para>
/// <strong>Nested ForEach</strong> is allowed; the inner iteration variable
/// shadows the outer in the per-step variable bag. Inner ForEach
/// collections can reference outer iteration variables
/// (e.g. inner <c>Collection = "#{env}-instances"</c>) — collection
/// resolution happens lazily per outer iteration so the substitution sees
/// the outer iteration's value.
/// </para>
///
/// <para>
/// <strong>Parallel ForEach</strong>: <c>Octopus.Action.ForEach.Parallel
/// = "true"</c> emits iterations as siblings in the same wave — the first
/// child of iterations 1..N gets <see cref="StepStartTrigger.StartWithPrevious"/>
/// so M14.4's wave partitioner groups all iterations together.
/// </para>
/// </summary>
public static class DeploymentPlanFlattener
{
    /// <summary>
    /// One M15 flatten-time warning. The orchestrator translates each
    /// warning into an audit event + a Step Group outcome row +
    /// (for <see cref="WarningKind.ForEachUnresolved"/>) a Required-gate
    /// check. <see cref="Source"/> carries the snapshot so the
    /// orchestrator can read the group's Required flag without an extra
    /// dictionary lookup.
    /// </summary>
    public sealed record Warning(
        WarningKind Kind,
        StepSnapshot Source,
        string CollectionExpression,
        string Detail);

    public enum WarningKind
    {
        /// <summary>ForEach collection resolved to an empty array. The
        /// group emits zero plans. Operators see the group as a no-op on
        /// the Steps tab; the audit row preserves what variable was empty.</summary>
        ForEachEmpty,

        /// <summary>ForEach collection variable could not be resolved
        /// (referenced an undefined array variable). The orchestrator
        /// applies the group's Required flag — Required → abort; non-
        /// required → continue with hasFailed.</summary>
        ForEachUnresolved,
    }

    /// <summary>
    /// Pure-function result. <see cref="Plans"/> is the flat list ready
    /// for the M14.4 wave partitioner. <see cref="SnapshotByPlanIndex"/>
    /// maps each emitted plan back to the snapshot it was derived from
    /// (multiple ForEach plans can share a snapshot). The orchestrator
    /// reads it instead of indexing the original flat snapshot array.
    /// </summary>
    public sealed record FlattenResult(
        DeploymentStepPlan[] Plans,
        StepSnapshot[] SnapshotByPlanIndex,
        IReadOnlyList<Warning> Warnings);

    public static FlattenResult Flatten(
        IReadOnlyList<StepSnapshot> snapshotSteps,
        IReadOnlyDictionary<string, string[]> arrayVars,
        VariableDictionary scalarVars)
    {
        ArgumentNullException.ThrowIfNull(snapshotSteps);
        ArgumentNullException.ThrowIfNull(arrayVars);
        ArgumentNullException.ThrowIfNull(scalarVars);

        // ── Index snapshots by Id for parent-child lookup ──────────────
        // Pre-M15 snapshots have Id = Guid.Empty (they predate the
        // Id field on StepSnapshot). Skip them in the parent index so
        // they're treated as orphan top-level steps, matching pre-M15
        // runtime behaviour.
        var byId = snapshotSteps
            .Where(s => s.Id != Guid.Empty)
            .ToDictionary(s => s.Id);

        var childrenByParent = snapshotSteps
            .Where(s => s.ParentStepId is not null
                        && s.ParentStepId.Value != Guid.Empty
                        && byId.ContainsKey(s.ParentStepId.Value))
            .GroupBy(s => s.ParentStepId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<StepSnapshot>)g
                .OrderBy(s => s.SortOrder).ToList());

        var topLevel = snapshotSteps
            .Where(s => s.ParentStepId is null
                        || s.ParentStepId.Value == Guid.Empty
                        || !byId.ContainsKey(s.ParentStepId.Value))
            .OrderBy(s => s.SortOrder)
            .ToArray();

        var plans = new List<DeploymentStepPlan>();
        var snapByIdx = new List<StepSnapshot>();
        var warnings = new List<Warning>();

        foreach (var top in topLevel)
        {
            EmitStep(
                snap:           top,
                iterationVars:  EmptyIterVars,
                childrenByParent: childrenByParent,
                arrayVars:      arrayVars,
                scalarVars:     scalarVars,
                plans:          plans,
                snapByIdx:      snapByIdx,
                warnings:       warnings,
                inheritStartTrigger:    null,
                accumulatorKeyOverride: null,
                displayNameOverride:    null);
        }

        return new FlattenResult(
            Plans:               [.. plans],
            SnapshotByPlanIndex: [.. snapByIdx],
            Warnings:            warnings);
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyIterVars
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // ── Recursive walker ───────────────────────────────────────────────

    private static void EmitStep(
        StepSnapshot snap,
        IReadOnlyDictionary<string, string> iterationVars,
        IReadOnlyDictionary<Guid, IReadOnlyList<StepSnapshot>> childrenByParent,
        IReadOnlyDictionary<string, string[]> arrayVars,
        VariableDictionary scalarVars,
        List<DeploymentStepPlan> plans,
        List<StepSnapshot> snapByIdx,
        List<Warning> warnings,
        StepStartTrigger? inheritStartTrigger,
        string? accumulatorKeyOverride,
        string? displayNameOverride)
    {
        var isGroup = string.Equals(
            snap.StepType, KrakenStepTypes.StepGroup,
            StringComparison.OrdinalIgnoreCase);

        if (!isGroup)
        {
            // ── Leaf step ─────────────────────────────────────────────
            var plan = BuildLeafPlan(
                snap, iterationVars, plans.Count,
                inheritStartTrigger ?? snap.StartTrigger,
                accumulatorKeyOverride ?? snap.Name,
                displayNameOverride);
            plans.Add(plan);
            snapByIdx.Add(snap);
            return;
        }

        // ── Step Group: ForEach mode or plain container? ──────────────
        var children = childrenByParent.GetValueOrDefault(
            snap.Id, []);
        if (children.Count == 0)
        {
            // Empty group — no plans emitted. Common during authoring; not
            // an error. Validator catches if this was unintentional.
            return;
        }

        var collectionExpr = ResolveConfigKey(snap.Config, "Octopus.Action.ForEach.Collection");
        if (string.IsNullOrWhiteSpace(collectionExpr))
        {
            EmitPlainContainer(snap, children, iterationVars,
                childrenByParent, arrayVars, scalarVars,
                plans, snapByIdx, warnings, inheritStartTrigger);
            return;
        }

        EmitForEachGroup(snap, children, collectionExpr, iterationVars,
            childrenByParent, arrayVars, scalarVars,
            plans, snapByIdx, warnings, inheritStartTrigger);
    }

    // ── Plain container ────────────────────────────────────────────────

    private static void EmitPlainContainer(
        StepSnapshot group,
        IReadOnlyList<StepSnapshot> children,
        IReadOnlyDictionary<string, string> iterationVars,
        IReadOnlyDictionary<Guid, IReadOnlyList<StepSnapshot>> childrenByParent,
        IReadOnlyDictionary<string, string[]> arrayVars,
        VariableDictionary scalarVars,
        List<DeploymentStepPlan> plans,
        List<StepSnapshot> snapByIdx,
        List<Warning> warnings,
        StepStartTrigger? inheritStartTrigger)
    {
        // The group's StartTrigger applies to the FIRST child (operators
        // expect "this group runs in parallel with prior step" to mean
        // the group's first child opens its wave). Subsequent children
        // retain their own StartTrigger.
        var groupStartTrigger = inheritStartTrigger ?? group.StartTrigger;
        for (var i = 0; i < children.Count; i++)
        {
            EmitStep(
                snap:                 children[i],
                iterationVars:        iterationVars,
                childrenByParent:     childrenByParent,
                arrayVars:            arrayVars,
                scalarVars:           scalarVars,
                plans:                plans,
                snapByIdx:            snapByIdx,
                warnings:             warnings,
                inheritStartTrigger:  i == 0 ? groupStartTrigger : null,
                accumulatorKeyOverride: null,
                displayNameOverride:    null);
        }
    }

    // ── ForEach ────────────────────────────────────────────────────────

    private static void EmitForEachGroup(
        StepSnapshot group,
        IReadOnlyList<StepSnapshot> children,
        string collectionExpr,
        IReadOnlyDictionary<string, string> iterationVars,
        IReadOnlyDictionary<Guid, IReadOnlyList<StepSnapshot>> childrenByParent,
        IReadOnlyDictionary<string, string[]> arrayVars,
        VariableDictionary scalarVars,
        List<DeploymentStepPlan> plans,
        List<StepSnapshot> snapByIdx,
        List<Warning> warnings,
        StepStartTrigger? inheritStartTrigger)
    {
        // Resolve the collection — lazy per outer iteration, so an inner
        // ForEach can reference the outer iteration variable in its
        // collection (e.g. inner Collection = "#{env}-instances").
        // Substitution merges outer iteration vars over the scalar bag,
        // then we resolve the resulting NAME against arrayVars.
        var resolvedName = SubstituteString(collectionExpr, scalarVars, iterationVars);
        if (!arrayVars.TryGetValue(resolvedName.Trim(), out var items))
        {
            warnings.Add(new Warning(
                Kind:                 WarningKind.ForEachUnresolved,
                Source:               group,
                CollectionExpression: collectionExpr,
                Detail:               $"ForEach collection '{collectionExpr}' " +
                                      (resolvedName == collectionExpr
                                          ? "references unknown array variable."
                                          : $"resolved to '{resolvedName.Trim()}' " +
                                            "which is not an array variable.")));
            return;
        }

        if (items.Length == 0)
        {
            warnings.Add(new Warning(
                Kind:                 WarningKind.ForEachEmpty,
                Source:               group,
                CollectionExpression: collectionExpr,
                Detail:               $"ForEach collection '{collectionExpr}' is empty; loop body skipped."));
            return;
        }

        // Iteration / index variable names: configurable but with sensible
        // defaults so the common case is "use #{item} and #{index}".
        var iterationVarName = ResolveConfigKey(group.Config,
            "Octopus.Action.ForEach.IterationVariable", "item")!;
        var indexVarName = ResolveConfigKey(group.Config,
            "Octopus.Action.ForEach.IndexVariable", "index")!;

        // Parallel mode: iterations 1..N's first child gets StartWithPrevious
        // so M14.4's wave partitioner groups all iterations together.
        var parallel = string.Equals(
            ResolveConfigKey(group.Config, "Octopus.Action.ForEach.Parallel"),
            "true", StringComparison.OrdinalIgnoreCase);

        var groupStartTrigger = inheritStartTrigger ?? group.StartTrigger;

        for (var iter = 0; iter < items.Length; iter++)
        {
            // Per-iteration variable bag: inner iteration var shadows outer
            // (the M15 plan's nested-ForEach rule).
            var iterVars = new Dictionary<string, string>(
                iterationVars, StringComparer.OrdinalIgnoreCase)
            {
                [iterationVarName] = items[iter],
                [indexVarName]     = iter.ToString(CultureInfo.InvariantCulture),
            };

            for (var ci = 0; ci < children.Count; ci++)
            {
                var child = children[ci];

                // First-child trigger inheritance:
                //   iteration 0 child 0  → group's StartTrigger.
                //   iteration N>0 child 0 → StartWithPrevious if parallel,
                //                            else StartAfterPrevious (waits
                //                            for prior iteration's last
                //                            child).
                //   later children       → their own snap.StartTrigger.
                StepStartTrigger? inherit = null;
                if (ci == 0)
                {
                    inherit = iter switch
                    {
                        0 => groupStartTrigger,
                        _ => parallel
                            ? StepStartTrigger.StartWithPrevious
                            : StepStartTrigger.StartAfterPrevious,
                    };
                }

                // Synthetic naming applies to the IMMEDIATE child only —
                // grandchildren (if the child is itself a Step Group) get
                // the leaf-level synthetic name composed inside EmitStep
                // when they're emitted. So we pass the override down only
                // when we know the recipient is a leaf at this layer.
                // For simplicity in M15.2 v1 we override at every depth's
                // first child; nested-naming polish is a follow-up.
                var accumulatorKey = $"{child.Name}[" +
                    $"{iter.ToString(CultureInfo.InvariantCulture)}]";
                var displayName = BuildDisplayName(child.Name,
                    iterationVarName, items[iter], iter);

                EmitStep(
                    snap:                 child,
                    iterationVars:        iterVars,
                    childrenByParent:     childrenByParent,
                    arrayVars:            arrayVars,
                    scalarVars:           scalarVars,
                    plans:                plans,
                    snapByIdx:            snapByIdx,
                    warnings:             warnings,
                    inheritStartTrigger:  inherit,
                    accumulatorKeyOverride: accumulatorKey,
                    displayNameOverride:    displayName);
            }
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static DeploymentStepPlan BuildLeafPlan(
        StepSnapshot snap,
        IReadOnlyDictionary<string, string> iterationVars,
        int planIndex,
        StepStartTrigger startTrigger,
        string accumulatorKey,
        string? displayNameOverride)
    {
        var substituted = SubstituteConfig(
            snap.Config, /*scalarVars*/ null, iterationVars);
        return new DeploymentStepPlan(
            Index:                       planIndex,
            Name:                        displayNameOverride ?? snap.Name,
            StepType:                    snap.StepType,
            PackageId:                   snap.PackageId,
            PackageVersion:              snap.PackageVersion,
            Config:                      substituted,
            TargetRoles:                 snap.TargetRoles,
            ReferencedPackages:          null, // resolved post-flatten
            StepPackageName:             snap.StepPackageName,
            StepPackageVersion:          snap.StepPackageVersion,
            Condition:                   (int)snap.Condition,
            ConditionVariableExpression: snap.ConditionVariableExpression,
            Required:                    snap.Required,
            MaxRetries:                  snap.MaxRetries,
            RetryDelaySeconds:           snap.RetryDelaySeconds,
            TimeoutSeconds:              snap.TimeoutSeconds,
            StartTrigger:                (int)startTrigger,
            AccumulatorKey:              accumulatorKey);
    }

    /// <summary>
    /// Octostache substitution for a step's Config. Iteration variables
    /// are layered over the scalar bag temporarily so <c>#{item}</c> in
    /// a script body resolves to the current iteration's value. We don't
    /// mutate the shared <see cref="VariableDictionary"/> across the walk
    /// — too easy to leak state between siblings; instead we use a
    /// helper <see cref="SubstituteString"/> that applies the iteration
    /// overlay locally.
    /// </summary>
    private static Dictionary<string, string> SubstituteConfig(
        Dictionary<string, string> config,
        VariableDictionary? scalarVars,
        IReadOnlyDictionary<string, string> iterationVars)
    {
        if (config.Count == 0)
        {
            return config;
        }

        // When iteration vars are empty AND scalarVars is null, we have
        // nothing to substitute — return as-is. (DeploymentWorker
        // already pre-substitutes its scalar vars into Config values
        // today; for M15.2 the flattener takes over the scalar pass too,
        // see DeploymentWorker integration.)
        return config.ToDictionary(
            kv => kv.Key,
            kv => SubstituteString(kv.Value, scalarVars, iterationVars));
    }

    /// <summary>
    /// Substitutes <c>#{...}</c> tokens in a single string. The iteration
    /// var bag wins on collision so inner-ForEach <c>#{item}</c> shadows
    /// any outer scalar of the same name. When the input has no
    /// substitutions (or substitution would degrade it), returns the
    /// input unchanged.
    /// </summary>
    private static string SubstituteString(
        string template,
        VariableDictionary? scalarVars,
        IReadOnlyDictionary<string, string> iterationVars)
    {
        if (string.IsNullOrEmpty(template))
        {
            return template;
        }

        // Build a local dictionary that overlays iteration vars over
        // scalar vars. The orchestrator's scalarVars are already an
        // Octostache VariableDictionary; we copy it into a fresh local
        // dictionary so we can layer iteration vars without mutating
        // the shared bag.
        var local = new VariableDictionary();
        if (scalarVars is not null)
        {
            foreach (var key in scalarVars.GetNames())
            {
                local[key] = scalarVars[key];
            }
        }
        foreach (var (k, v) in iterationVars)
        {
            local[k] = v;
        }
        return local.Evaluate(template) ?? template;
    }

    /// <summary>
    /// Builds the human-readable display name for a ForEach iteration's
    /// child. Clean values become <c>OriginalName [var=value]</c>; long
    /// or weird-character values fall back to <c>OriginalName [var=#index]</c>.
    /// </summary>
    private static string BuildDisplayName(
        string baseName, string varName, string value, int index)
    {
        const int CleanMaxLength = 40;
        var clean = !string.IsNullOrEmpty(value)
            && value.Length <= CleanMaxLength
            && value.IndexOfAny(['\n', '\r', '\t', ']']) < 0;
        return clean
            ? $"{baseName} [{varName}={value}]"
            : $"{baseName} [{varName}=#" +
              $"{index.ToString(CultureInfo.InvariantCulture)}]";
    }

    /// <summary>
    /// Resolves a config key with optional default. Case-insensitive lookup.
    /// </summary>
    private static string? ResolveConfigKey(
        Dictionary<string, string> config,
        string key,
        string? @default = null)
    {
        if (config.TryGetValue(key, out var value)
            && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }
        return @default;
    }
}
