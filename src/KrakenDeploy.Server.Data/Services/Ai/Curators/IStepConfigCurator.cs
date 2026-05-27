namespace KrakenDeploy.Server.Data.Services.Ai.Curators;

/// <summary>
/// M11.B — projects a step's raw Config dictionary down to a slim,
/// high-signal summary for LLM consumption. A step's full Config can carry
/// 30+ keys (especially after an Octopus import); piping all of that into
/// the model burns context-window tokens on noise. Each curator owns one
/// or more step types (declared via <see cref="CuratesStepTypeAttribute"/>)
/// and emits the 3-5 keys that actually matter for understanding what the
/// step does.
/// <para>
/// <strong>Drill-down</strong>: the curated summary is the default view.
/// When the AI needs the full Config for troubleshooting, it reads the
/// step's <c>fullConfigUri</c> Resource (added in the MCP Resources commit)
/// or calls the <c>get_step_config</c> tool — both return the unredacted
/// dictionary. Curation never loses data; it just defers the bulk.
/// </para>
/// <para>
/// <strong>Registration</strong>: curators are discovered via DI — register
/// each as <c>IStepConfigCurator</c> and
/// <see cref="StepConfigCuratorRegistry"/> reads its
/// <see cref="CuratesStepTypeAttribute"/>s to build the step-type → curator
/// map. A new step package ships its curator alongside the step and adds
/// one DI line; no central catalog to edit.
/// </para>
/// </summary>
public interface IStepConfigCurator
{
    /// <summary>
    /// Returns the slim summary for <paramref name="config"/>. Implementations
    /// must be pure (no IO, no mutation of the input) and tolerant of
    /// missing keys — a half-authored step still curates to whatever keys
    /// are present.
    /// </summary>
    IReadOnlyDictionary<string, string> Curate(IReadOnlyDictionary<string, string> config);
}

/// <summary>
/// Declares which step type(s) a <see cref="IStepConfigCurator"/> handles.
/// Multiple attributes on one curator cover aliases (e.g. the script
/// curator handles both <c>Octopus.Script</c> and <c>Kraken.Script</c>).
/// Matched case-insensitively by <see cref="StepConfigCuratorRegistry"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class CuratesStepTypeAttribute(string stepType) : Attribute
{
    public string StepType { get; } = stepType;
}
