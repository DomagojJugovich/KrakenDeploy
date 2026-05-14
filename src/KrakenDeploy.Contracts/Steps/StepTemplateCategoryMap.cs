using System.Reflection;
using System.Text.Json;

namespace KrakenDeploy.Contracts.Steps;

/// <summary>
/// Maps the small-bucket <c>Category</c> from an Octopus Library step-template
/// JSON (e.g. <c>"aws"</c>, <c>"iis"</c>, <c>"windows-iis"</c>) to the
/// big-bucket display category surfaced in the KrakenDeploy step picker
/// ("Development and Scripting", "Containers and Orchestration",
/// "Cloud Native Services", "Infrastructure as Code", …).
/// <para>
/// The mapping table is the embedded resource <c>category-mapping.json</c>
/// next to this class; keep that file as the source of truth and this class
/// purely as the runtime accessor.
/// </para>
/// </summary>
public static class StepTemplateCategoryMap
{
    /// <summary>The fallback bucket for any category not present in the table.</summary>
    public const string Other = "Other";

    private static readonly Lazy<IReadOnlyDictionary<string, string>> _smallToBig =
        new(LoadFromEmbeddedResource);

    private static readonly Lazy<IReadOnlyList<string>> _bigBuckets =
        new(() => [.. _smallToBig.Value.Values.Distinct().OrderBy(s => s, StringComparer.OrdinalIgnoreCase), Other]);

    /// <summary>
    /// Returns the big-bucket category for the given small-bucket value, or
    /// <see cref="Other"/> when the small-bucket is unmapped / null / empty.
    /// Match is case-insensitive on the small-bucket key.
    /// </summary>
    public static string GetBigBucket(string? smallCategory)
    {
        if (string.IsNullOrWhiteSpace(smallCategory))
        {
            return Other;
        }

        return _smallToBig.Value.TryGetValue(smallCategory.Trim(), out var big)
            ? big
            : Other;
    }

    /// <summary>
    /// All big-bucket category names in stable display order, with
    /// <see cref="Other"/> always last. Useful for populating filter panels.
    /// </summary>
    public static IReadOnlyList<string> BigBuckets => _bigBuckets.Value;

    /// <summary>
    /// Every (small, big) pair from the embedded mapping table. Useful for
    /// debugging / admin pages that want to surface coverage.
    /// </summary>
    public static IReadOnlyDictionary<string, string> All => _smallToBig.Value;

    // ── Loader ────────────────────────────────────────────────────────────

    private static Dictionary<string, string> LoadFromEmbeddedResource()
    {
        var asm = typeof(StepTemplateCategoryMap).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("category-mapping.json", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "Embedded resource 'category-mapping.json' not found. " +
                "Check the EmbeddedResource entry in KrakenDeploy.Contracts.csproj.");

        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Cannot open embedded resource '{name}'.");
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();

        using var doc = JsonDocument.Parse(json);
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            // Skip metadata keys like "_comment".
            if (prop.Name.StartsWith('_'))
            {
                continue;
            }
            if (prop.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in prop.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                var small = item.GetString();
                if (!string.IsNullOrWhiteSpace(small))
                {
                    // Last write wins if a small bucket is listed under multiple bigs;
                    // the JSON should not do that, but tolerate it without throwing.
                    dict[small.Trim()] = prop.Name;
                }
            }
        }

        return dict;
    }
}
