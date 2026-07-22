namespace KrakenDeploy.Contracts.Steps;

/// <summary>
/// Octopus-compatible Config keys for the <c>Kraken.StepGroup</c> step type —
/// the loop (<c>ForEach.*</c>) and rolling-window (<c>MaxParallelism</c>)
/// properties.
/// <para>
/// D3 (2026-07): <see cref="MaxParallelism"/>, <see cref="ForEachCollection"/>
/// and <see cref="ForEachParallel"/> are promoted to typed columns on
/// <c>ProcessStep</c> / <c>StepSnapshot</c>; the engine branches on those
/// columns, never on these string keys. The keys survive ONLY at the Octopus
/// import/export boundary (and the REST/authoring boundary), which is why the
/// literals are centralised here instead of being scattered as inline strings.
/// <see cref="ForEachIterationVariable"/> and <see cref="ForEachIndexVariable"/>
/// remain free-form Config strings (not promoted) and are listed here purely so
/// the boundary code has one place for every step-group key.
/// </para>
/// </summary>
public static class KrakenStepGroupConfigKeys
{
    /// <summary>Rolling-window fan-out cap (positive integer). Promoted to the
    /// typed <c>int? MaxParallelism</c> column.</summary>
    public const string MaxParallelism = "Octopus.Action.MaxParallelism";

    /// <summary>Name (or Octostache expression) of the array variable a ForEach
    /// Step Group iterates. Promoted to the typed <c>string? ForEachCollection</c>
    /// column. Blank/absent = plain container.</summary>
    public const string ForEachCollection = "Octopus.Action.ForEach.Collection";

    /// <summary>When <c>"true"</c>, ForEach iterations dispatch as one parallel
    /// wave. Promoted to the typed <c>bool ForEachParallel</c> column.</summary>
    public const string ForEachParallel = "Octopus.Action.ForEach.Parallel";

    /// <summary>Iteration variable name (default <c>item</c>). NOT promoted —
    /// stays a Config string.</summary>
    public const string ForEachIterationVariable = "Octopus.Action.ForEach.IterationVariable";

    /// <summary>Index variable name (default <c>index</c>). NOT promoted — stays
    /// a Config string.</summary>
    public const string ForEachIndexVariable = "Octopus.Action.ForEach.IndexVariable";
}
