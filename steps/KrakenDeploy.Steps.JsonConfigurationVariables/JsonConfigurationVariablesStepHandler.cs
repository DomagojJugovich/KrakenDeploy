using System.Text.Json;
using System.Text.Json.Nodes;
using KrakenDeploy.Contracts.Steps;

namespace KrakenDeploy.Steps.JsonConfigurationVariables;

/// <summary>
/// Handles <c>Octopus.JsonConfigurationVariables</c> — applies Octopus's
/// "JSON Configuration Variables" feature to JSON files inside the extracted
/// package. Canonical home for this step type (Phase D-8); the legacy
/// in-DI <c>FileTransformStepHandler</c> + matching <c>Octopus.FileTransform</c>
/// step type were retired in favour of this naming so the vocabulary matches
/// what Octopus's docs actually call this feature. XDT (XML) transforms live
/// separately on the <c>Octopus.TentaclePackage</c> step (Octopus's own model).
/// <para>
/// Reads <c>Octopus.Action.Package.JsonConfigurationVariablesTargets</c> —
/// a newline- or comma-separated list of JSON file glob patterns. For each
/// matching file, walks the deployment variable dictionary; any variable
/// whose name maps to a dot-separated JSON path (e.g. <c>App.Db.Host</c>
/// → <c>App → Db → Host</c>) replaces the existing value at that path.
/// Case-insensitive key match.
/// </para>
/// </summary>
public sealed class JsonConfigurationVariablesStepHandler : IStepHandler
{
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    public bool CanHandle(string stepType)
        => stepType.Equals("Octopus.JsonConfigurationVariables", StringComparison.OrdinalIgnoreCase);

    public bool RequiresPackage => true;

    public async Task<bool> HandleAsync(StepHandlerContext context, CancellationToken ct)
    {
        context.Step.Config.TryGetValue(
            "Octopus.Action.Package.JsonConfigurationVariablesTargets",
            out var targetsRaw);

        if (string.IsNullOrWhiteSpace(targetsRaw))
        {
            await context.LogAsync("warning",
                "No JSON config targets specified in " +
                "Octopus.Action.Package.JsonConfigurationVariablesTargets — nothing to transform.")
                .ConfigureAwait(false);
            return true;
        }

        var patterns = SplitPatterns(targetsRaw);
        var allOk    = true;

        foreach (var pattern in patterns)
        {
            var files = ResolveGlob(context.ExtractDir, pattern);
            if (files.Count == 0)
            {
                await context.LogAsync("warning",
                    $"No files matched pattern '{pattern}'.").ConfigureAwait(false);
                continue;
            }

            foreach (var filePath in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var json = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
                    var node = JsonNode.Parse(json);
                    if (node is null)
                    {
                        await context.LogAsync("warning",
                            $"'{filePath}' is empty or null — skipping.").ConfigureAwait(false);
                        continue;
                    }

                    var changed = ApplyVariables(node, context.Plan.Variables);
                    if (changed > 0)
                    {
                        var updated = node.ToJsonString(WriteOpts);
                        await File.WriteAllTextAsync(filePath, updated, ct).ConfigureAwait(false);
                        var rel = Path.GetRelativePath(context.ExtractDir, filePath);
                        await context.LogAsync("info",
                            $"Applied {changed} variable(s) to '{rel}'.").ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    await context.LogAsync("error",
                        $"Failed to transform '{filePath}': {ex.Message}").ConfigureAwait(false);
                    allOk = false;
                }
            }
        }

        return allOk;
    }

    // ── JSON variable application ──────────────────────────────────────────

    /// <summary>
    /// Walks the variable dictionary and applies each variable whose name
    /// represents a dot-separated JSON path to the document tree.
    /// Returns the number of values that were replaced.
    /// </summary>
    private static int ApplyVariables(
        JsonNode root,
        IReadOnlyDictionary<string, string> variables)
    {
        var count = 0;
        foreach (var (name, value) in variables)
        {
            var segments = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (SetJsonPath(root, segments, value))
            {
                count++;
            }
        }
        return count;
    }

    private static bool SetJsonPath(JsonNode node, ReadOnlySpan<string> segments, string value)
    {
        if (segments.IsEmpty || node is not JsonObject obj)
        {
            return false;
        }

        var key = segments[0];
        if (segments.Length == 1)
        {
            // Case-insensitive match on the property name — preserves the
            // original casing in the rewritten file.
            var matchingKey = obj.FirstOrDefault(
                kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Key;
            if (matchingKey is null) { return false; }

            obj[matchingKey] = JsonValue.Create(value);
            return true;
        }

        // Recurse into nested objects.
        var childKey = obj.FirstOrDefault(
            kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Key;
        if (childKey is null || obj[childKey] is not JsonNode child)
        {
            return false;
        }
        return SetJsonPath(child, segments[1..], value);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static List<string> SplitPatterns(string raw)
        => [.. raw.Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries)
                  .Select(p => p.Trim())
                  .Where(p => !string.IsNullOrEmpty(p))];

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static List<string> ResolveGlob(string baseDir, string pattern)
    {
        // The pattern is operator-supplied config; canonicalise everything and
        // confine it to the extracted package so "../../etc/*" can't escape
        // ExtractDir and rewrite arbitrary files.
        var baseFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDir));

        if (!pattern.Contains('*') && !pattern.Contains('?'))
        {
            var exact = Path.GetFullPath(Path.Combine(baseDir, pattern));
            return IsWithinBase(baseFull, exact) && File.Exists(exact) ? [exact] : [];
        }

        var lastSlash = pattern.LastIndexOfAny(['/', '\\']);
        var dirPart   = lastSlash >= 0 ? pattern[..lastSlash] : string.Empty;
        var fileGlob  = lastSlash >= 0 ? pattern[(lastSlash + 1)..] : pattern;
        var recursive = pattern.Contains("**");

        var dir = Path.GetFullPath(Path.Combine(baseDir, dirPart));
        if (!IsWithinBase(baseFull, dir) || !Directory.Exists(dir))
        {
            return [];
        }
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return [.. Directory.GetFiles(dir, fileGlob, option)
                            .Where(f => IsWithinBase(baseFull, Path.GetFullPath(f)))];
    }

    /// <summary>True when <paramref name="candidateFull"/> is the base directory itself or sits inside it.</summary>
    private static bool IsWithinBase(string baseFull, string candidateFull)
    {
        var c = Path.TrimEndingDirectorySeparator(candidateFull);
        if (string.Equals(c, baseFull, PathComparison))
        {
            return true;
        }
        return c.Length > baseFull.Length
            && c.StartsWith(baseFull, PathComparison)
            && (c[baseFull.Length] == Path.DirectorySeparatorChar
                || c[baseFull.Length] == Path.AltDirectorySeparatorChar);
    }
}
