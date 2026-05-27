using System.Globalization;

namespace KrakenDeploy.Server.Data.Services.Ai.Curators;

/// <summary>
/// Fallback curator for step types no dedicated curator claims (custom
/// step packages without their own curator, legacy types, etc.). Emits a
/// key count + a small alphabetical sample of key names — enough for the
/// AI to know "this step has config X, Y, Z, … (12 keys total)" and decide
/// whether to drill into the full Config via the step's <c>fullConfigUri</c>.
/// <para>
/// Deliberately does NOT emit values — for an unknown step type we can't
/// know which keys are safe (a value could be a connection string or a
/// token). The sample is key NAMES only; the full Config drill-down is the
/// audited, permission-gated path that surfaces values.
/// </para>
/// </summary>
public sealed class DefaultStepConfigCurator : IStepConfigCurator
{
    private const int SampleSize = 5;

    public IReadOnlyDictionary<string, string> Curate(IReadOnlyDictionary<string, string> config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var sampleKeys = config.Keys
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Take(SampleSize)
            .ToArray();

        var summary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["_configKeyCount"] = config.Count.ToString(CultureInfo.InvariantCulture),
        };
        if (sampleKeys.Length > 0)
        {
            summary["_configKeySample"] = string.Join(", ", sampleKeys)
                + (config.Count > sampleKeys.Length ? ", …" : "");
        }
        return summary;
    }
}
