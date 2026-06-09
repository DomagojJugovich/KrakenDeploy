using System.Globalization;
using Octostache;

namespace KrakenDeploy.Execution;

/// <summary>
/// Helpers for building an Octostache <see cref="VariableDictionary"/> from the
/// plain string dictionaries the deployment/runbook plumbing passes around.
/// </summary>
public static class VariableDictionaryExtensions
{
    /// <summary>
    /// Copies a flat string map into a fresh <see cref="VariableDictionary"/>.
    /// <para>
    /// This is the scalar conversion only — one entry per key. It is NOT the
    /// array-aware variable-bag build the orchestrator does when resolving a
    /// deployment's variables (which additionally expands a StringArray into
    /// <c>name[i]</c> indexed entries); callers that need that must keep their
    /// own loop.
    /// </para>
    /// </summary>
    public static VariableDictionary ToVariableDictionary(
        this IReadOnlyDictionary<string, string> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var dict = new VariableDictionary();
        foreach (var (key, value) in source)
        {
            dict[key] = value;
        }
        return dict;
    }

    /// <summary>
    /// Scalar conversion PLUS the array-index expansion the deployment
    /// orchestrator applies when resolving a deployment's variables: every
    /// StringArray entry in <paramref name="arrays"/> contributes
    /// <c>name[i]</c> indexed keys so Octostache expressions like
    /// <c>#{MyArray[0]}</c> resolve.
    /// <para>
    /// This is the single source of truth for the <c>name[i]</c> key format.
    /// The online server builds its condition <c>varDict</c> inline in
    /// <c>DeploymentWorker.BuildTargetDispatchContextAsync</c> and the offline
    /// runner builds its condition bag in
    /// <c>DeploymentExecutor.RunStepInWaveAsync</c>; both MUST produce
    /// byte-identical keys or a <c>Condition=Variable</c> step referencing an
    /// indexed element makes opposite Run/Skip decisions online vs offline.
    /// </para>
    /// <para>
    /// Scalars win on key collision: callers pass the comma-joined scalar form
    /// of an array under its bare <c>name</c> in <paramref name="scalars"/>
    /// (matching the server), so <c>name</c> keeps the joined value and only
    /// the <c>name[i]</c> keys come from <paramref name="arrays"/>.
    /// </para>
    /// </summary>
    public static VariableDictionary ToVariableDictionary(
        this IReadOnlyDictionary<string, string> scalars,
        IReadOnlyDictionary<string, string[]> arrays)
    {
        ArgumentNullException.ThrowIfNull(arrays);

        var dict = scalars.ToVariableDictionary();
        AddArrayIndexEntries(dict, arrays);
        return dict;
    }

    /// <summary>
    /// Adds <c>name[i]</c> indexed entries for each StringArray to an existing
    /// dictionary. Mirrors the server's expansion (incl.
    /// <see cref="CultureInfo.InvariantCulture"/> on the index) so the
    /// generated keys are identical online and offline. Exposed so the server
    /// path can adopt the same formatter.
    /// </summary>
    public static void AddArrayIndexEntries(
        VariableDictionary dict,
        IReadOnlyDictionary<string, string[]> arrays)
    {
        ArgumentNullException.ThrowIfNull(dict);
        ArgumentNullException.ThrowIfNull(arrays);

        foreach (var (name, items) in arrays)
        {
            for (var i = 0; i < items.Length; i++)
            {
                dict[$"{name}[{i.ToString(CultureInfo.InvariantCulture)}]"] = items[i];
            }
        }
    }
}
