namespace KrakenDeploy.Execution;

/// <summary>
/// Pure trigger-based wave grouping shared by the server orchestrator's
/// <c>WavePartitioner</c> and the offline agent runner's
/// <c>DeploymentExecutor</c>. A wave = the first step plus every following
/// step whose Start Trigger is "start with the previous step", until a
/// "start after previous" step opens the next wave. The first step's trigger
/// is ignored (it has no predecessor). Steps are ordered by their index
/// before grouping.
///
/// <para>
/// Generic over the step type and parameterised over an index selector + a
/// "starts with the previous step" predicate, so it carries no dependency on
/// the wire contract or the server domain. Wave CLASSIFICATION (server-side
/// vs target-side) and mixed-wave validation stay with the server's
/// <c>WavePartitioner</c> — those are online-only concerns the offline,
/// single-side runner does not share.
/// </para>
/// </summary>
public static class WaveGrouping
{
    /// <summary>
    /// Groups <paramref name="steps"/> into waves by their Start Trigger.
    /// </summary>
    /// <param name="steps">The flat step list, in any order — sorted by
    /// <paramref name="indexSelector"/> ascending internally.</param>
    /// <param name="indexSelector">Returns a step's ordering index.</param>
    /// <param name="startsWithPrevious">Whether a step runs alongside the
    /// previous step (joins the current wave) rather than opening a new one.
    /// Only evaluated for steps after the first — the first step always opens
    /// the first wave regardless of its trigger.</param>
    public static List<List<TStep>> Partition<TStep>(
        IReadOnlyList<TStep> steps,
        Func<TStep, int> indexSelector,
        Func<TStep, bool> startsWithPrevious)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(indexSelector);
        ArgumentNullException.ThrowIfNull(startsWithPrevious);

        var waves = new List<List<TStep>>();
        if (steps.Count == 0)
        {
            return waves;
        }

        var ordered = steps.OrderBy(indexSelector).ToList();
        var current = new List<TStep> { ordered[0] };

        for (var i = 1; i < ordered.Count; i++)
        {
            if (startsWithPrevious(ordered[i]))
            {
                current.Add(ordered[i]);
            }
            else
            {
                waves.Add(current);
                current = [ordered[i]];
            }
        }
        waves.Add(current);
        return waves;
    }
}
