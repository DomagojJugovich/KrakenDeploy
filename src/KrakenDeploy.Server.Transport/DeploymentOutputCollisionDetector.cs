namespace KrakenDeploy.Server.Transport;

/// <summary>
/// M14.4 — detects output-variable collisions inside a parallel wave. Two
/// or more parallel siblings (Start Trigger = StartWithPrevious) writing
/// to the same output-variable name resolve last-writer-wins in
/// <see cref="DeploymentStepPlan.Index"/> order (SortOrder-rank), as
/// locked in the M14.4 plan. The DB storage stays per-step (every
/// <c>DeploymentOutputVariable</c> row keys by step name), so collisions
/// are purely a forensic signal: scripts referencing the variable
/// <em>without</em> the step-name qualifier — or future code paths
/// projecting all steps' outputs into one bag — read the winning value
/// and a record exists of who lost.
///
/// <para>
/// The orchestrator uses the returned <see cref="Collision"/> list to
/// emit one <c>Deployment.ParallelOutputCollision</c> audit event +
/// warning log line per collision. Without a collision (the common
/// case — synthetic names per step or non-overlapping output sets)
/// the detector returns an empty list and the orchestrator emits
/// nothing.
/// </para>
/// </summary>
public static class DeploymentOutputCollisionDetector
{
    /// <summary>
    /// One detected collision: <see cref="VariableName"/> was written by
    /// more than one step in the wave. <see cref="Writers"/> lists every
    /// writer in <em>SortOrder</em>; the last entry is the winner whose
    /// value the unqualified reference (and any future flat-namespace
    /// projection) takes.
    /// </summary>
    public sealed record Collision(
        string VariableName,
        IReadOnlyList<Writer> Writers)
    {
        /// <summary>Convenience accessor for the winning writer
        /// (last-writer-wins by SortOrder).</summary>
        public Writer Winner => Writers[Writers.Count - 1];

        /// <summary>Convenience accessor for the losing writers (every
        /// writer except the last).</summary>
        public IEnumerable<Writer> Losers => Writers.Take(Writers.Count - 1);
    }

    /// <summary>
    /// One per-step writer of a colliding variable. <see cref="StepName"/>
    /// is the step that emitted the value; <see cref="Value"/> is the
    /// captured string (no truncation — caller decides whether to elide
    /// for log lines).
    /// </summary>
    public sealed record Writer(string StepName, string Value);

    /// <summary>
    /// Inspects each step's captured-output bucket and returns every
    /// variable name written by more than one step. Writers are ordered
    /// by the iteration order of <paramref name="bucketsBySortOrder"/> —
    /// the caller MUST pass an enumerable already sorted by
    /// <see cref="DeploymentStepPlan.Index"/> (SortOrder) so the
    /// last-writer-wins contract is well-defined.
    ///
    /// <para>
    /// Variable-name comparison is case-insensitive (mirrors Octopus +
    /// the Octostache <c>VariableDictionary</c> contract).
    /// </para>
    /// </summary>
    /// <param name="bucketsBySortOrder">Per-step output buckets, MUST be
    /// in SortOrder. Each entry is the step's name plus its captured
    /// outputs (as reported by <c>ReportStepCompletedAsync</c>).</param>
    public static List<Collision> Detect(
        IEnumerable<(string StepName, IReadOnlyDictionary<string, string> Outputs)>
            bucketsBySortOrder)
    {
        ArgumentNullException.ThrowIfNull(bucketsBySortOrder);

        // Accumulate every (variable -> [writers...]) in encounter order
        // (which the caller guaranteed is SortOrder). Dictionary key
        // comparison is case-insensitive so "Foo" / "foo" collide.
        var writersByName = new Dictionary<string, List<Writer>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var (stepName, outputs) in bucketsBySortOrder)
        {
            if (outputs is null || outputs.Count == 0)
            {
                continue;
            }
            foreach (var (name, value) in outputs)
            {
                if (!writersByName.TryGetValue(name, out var list))
                {
                    list = [];
                    writersByName[name] = list;
                }
                list.Add(new Writer(stepName, value));
            }
        }

        var collisions = new List<Collision>();
        foreach (var (name, writers) in writersByName)
        {
            if (writers.Count >= 2)
            {
                collisions.Add(new Collision(name, writers));
            }
        }
        return collisions;
    }
}
