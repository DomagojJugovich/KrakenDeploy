using System.Globalization;

namespace KrakenDeploy.Server.Core.Domain.Processes;

/// <summary>
/// M15 follow-up — pure function that flattens a tree of
/// <see cref="IComposableStep"/> rows into a DFS-ordered render sequence
/// (parent immediately followed by its children, then back to the next
/// top-level step), with sidecar maps for the template:
/// <list type="bullet">
///   <item><b>Depth</b> — child-row indent on the editor.</item>
///   <item><b>Numbering</b> — dotted form (<c>1</c>, <c>1.1</c>, <c>1.2</c>,
///         <c>2</c>, …) shown in the number column.</item>
///   <item><b>Child counts</b> — Step Group rows display
///         <c>"(N children)"</c>.</item>
///   <item><b>Descendants</b> — drag-into-row reparenting filters out
///         the dragged step's own descendants as drop targets so the
///         operator can't create a cycle by dragging a parent onto
///         one of its own children.</item>
/// </list>
///
/// <para>
/// Generic over the entity type so both <c>DeploymentStep</c> (process
/// editor) and <c>RunbookStep</c> (runbook editor) use the same logic
/// without projection. <see cref="IEnumerable{T}"/> covariance lets
/// callers pass <c>IEnumerable&lt;DeploymentStep&gt;</c> or
/// <c>IEnumerable&lt;RunbookStep&gt;</c> directly.
/// </para>
/// </summary>
public static class StepTreeBuilder
{
    /// <summary>
    /// Builds the DFS-ordered tree view over <paramref name="rawSteps"/>.
    /// Top-level steps (null or empty <see cref="IComposableStep.ParentStepId"/>)
    /// are walked in <see cref="IComposableStep"/>-supplied order — the
    /// caller is expected to pass a list pre-sorted by <c>SortOrder</c>
    /// (the entity carries it; this interface deliberately does not, so
    /// callers retain control over the sort key).
    ///
    /// <para>
    /// Empty or null input returns an empty view (no allocations
    /// beyond the empty maps).
    /// </para>
    /// </summary>
    public static StepTreeView<T> Build<T>(IEnumerable<T> rawSteps)
        where T : IComposableStep
    {
        ArgumentNullException.ThrowIfNull(rawSteps);
        var stepsList = rawSteps as IReadOnlyList<T> ?? rawSteps.ToList();

        if (stepsList.Count == 0)
        {
            return StepTreeView.Empty<T>();
        }

        // Group children by parent so DFS lookup is O(1).
        var byParent = new Dictionary<Guid, List<T>>();
        var orphans = new List<T>();
        foreach (var s in stepsList)
        {
            var parentId = s.ParentStepId;
            if (parentId is null)
            {
                orphans.Add(s);
                continue;
            }
            if (!byParent.TryGetValue(parentId.Value, out var list))
            {
                list = [];
                byParent[parentId.Value] = list;
            }
            list.Add(s);
        }

        // Caller-supplied order already reflects SortOrder; preserve it
        // for top-level + sibling children.
        var ordered = new List<T>(stepsList.Count);
        var depth = new Dictionary<Guid, int>();
        var numbering = new Dictionary<Guid, string>();
        var childCount = new Dictionary<Guid, int>();
        var descendants = new Dictionary<Guid, HashSet<Guid>>();

        void Walk(IReadOnlyList<T> siblings, int currentDepth, string prefix)
        {
            for (var i = 0; i < siblings.Count; i++)
            {
                var s = siblings[i];
                var label = (i + 1).ToString(CultureInfo.InvariantCulture);
                var number = string.IsNullOrEmpty(prefix)
                    ? label
                    : $"{prefix}.{label}";

                ordered.Add(s);
                depth[s.Id] = currentDepth;
                numbering[s.Id] = number;

                if (byParent.TryGetValue(s.Id, out var kids) && kids.Count > 0)
                {
                    childCount[s.Id] = kids.Count;
                    Walk(kids, currentDepth + 1, number);
                }
            }
        }
        Walk(orphans, 0, "");

        // Steps that referenced a parent we didn't find (corrupted data
        // or orphans from a deleted parent) — surface them as top-level
        // appended after the legit top-level set, so the operator can
        // still see + fix them. They get a "?" prefix to flag the issue.
        foreach (var s in stepsList)
        {
            if (s.ParentStepId is { } pid
                && !depth.ContainsKey(s.Id)
                && !orphans.Any(o => o.Id == s.Id))
            {
                ordered.Add(s);
                depth[s.Id] = 0;
                numbering[s.Id] = "?";
            }
        }

        // Compute descendants per step: post-order traversal accumulating
        // child + descendant ids. Used by the drag UX to forbid drops on
        // descendants of the source.
        foreach (var s in stepsList)
        {
            BuildDescendantSet(s.Id, byParent, descendants);
        }

        return new StepTreeView<T>(
            OrderedSteps:        ordered,
            DepthByStepId:       depth,
            NumberingByStepId:   numbering,
            ChildCountByStepId:  childCount,
            DescendantsByStepId: descendants);
    }

    private static HashSet<Guid> BuildDescendantSet<T>(
        Guid stepId,
        Dictionary<Guid, List<T>> byParent,
        Dictionary<Guid, HashSet<Guid>> memo)
        where T : IComposableStep
    {
        if (memo.TryGetValue(stepId, out var existing))
        {
            return existing;
        }

        var result = new HashSet<Guid>();
        memo[stepId] = result; // memoise before recursion to break cycles defensively
        if (byParent.TryGetValue(stepId, out var kids))
        {
            foreach (var k in kids)
            {
                result.Add(k.Id);
                foreach (var d in BuildDescendantSet(k.Id, byParent, memo))
                {
                    result.Add(d);
                }
            }
        }
        return result;
    }
}

/// <summary>
/// Result of <see cref="StepTreeBuilder.Build{T}"/>. All maps are keyed
/// by step id; missing entries should be treated as "depth 0, no children,
/// no descendants" — the same defaults as a top-level leaf.
/// </summary>
public sealed record StepTreeView<T>(
    IReadOnlyList<T> OrderedSteps,
    IReadOnlyDictionary<Guid, int> DepthByStepId,
    IReadOnlyDictionary<Guid, string> NumberingByStepId,
    IReadOnlyDictionary<Guid, int> ChildCountByStepId,
    IReadOnlyDictionary<Guid, HashSet<Guid>> DescendantsByStepId)
    where T : IComposableStep;

/// <summary>
/// Non-generic factory for empty <see cref="StepTreeView{T}"/> instances.
/// Lives outside the generic type to satisfy CA1000 (no static members
/// on generic types).
/// </summary>
public static class StepTreeView
{
    public static StepTreeView<T> Empty<T>() where T : IComposableStep => new(
        OrderedSteps:        [],
        DepthByStepId:       new Dictionary<Guid, int>(),
        NumberingByStepId:   new Dictionary<Guid, string>(),
        ChildCountByStepId:  new Dictionary<Guid, int>(),
        DescendantsByStepId: new Dictionary<Guid, HashSet<Guid>>());
}
