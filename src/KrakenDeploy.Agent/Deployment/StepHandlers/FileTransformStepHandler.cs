using System.Text.Json;
using System.Text.Json.Nodes;

namespace KrakenDeploy.Agent.Deployment.StepHandlers;

/// <summary>
/// Handles <c>Octopus.FileTransform</c> step type.
/// <para>
/// Reads <c>Octopus.Action.Package.JsonConfigurationVariablesTargets</c> — a newline-
/// or comma-separated list of JSON file glob patterns — and replaces property values
/// whose colon-separated path matches a resolved variable name.
/// </para>
/// <para>
/// Example: variable <c>MyApp.Database.ConnectionString</c> is applied to the JSON
/// path <c>MyApp → Database → ConnectionString</c> in each matching file.
/// This mirrors Octopus's "JSON Configuration Variables" feature.
/// </para>
/// </summary>
public sealed class FileTransformStepHandler : IStepHandler
{
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    public bool CanHandle(string stepType)
        => stepType.Equals("Octopus.FileTransform", StringComparison.OrdinalIgnoreCase);

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

    // ── JSON variable application ──────────────────────────────────────────────

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
            // Variable name → path segments (e.g. "App.Db.Host" → ["App","Db","Host"]).
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
        if (segments.IsEmpty)
        {
            return false;
        }

        if (node is not JsonObject obj)
        {
            return false;
        }

        var key = segments[0];
        if (segments.Length == 1)
        {
            // Find a case-insensitive match.
            var matchingKey = obj.FirstOrDefault(
                kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Key;

            if (matchingKey is null)
            {
                return false;
            }

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

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static List<string> SplitPatterns(string raw)
        => raw.Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries)
              .Select(p => p.Trim())
              .Where(p => !string.IsNullOrEmpty(p))
              .ToList();

    private static List<string> ResolveGlob(string baseDir, string pattern)
    {
        if (!pattern.Contains('*') && !pattern.Contains('?'))
        {
            var exact = Path.Combine(baseDir, pattern);
            return File.Exists(exact) ? [exact] : [];
        }

        var lastSlash = pattern.LastIndexOfAny(['/', '\\']);
        var dir       = lastSlash >= 0 ? Path.Combine(baseDir, pattern[..lastSlash]) : baseDir;
        var fileGlob  = lastSlash >= 0 ? pattern[(lastSlash + 1)..] : pattern;
        var recursive = pattern.Contains("**");

        if (!Directory.Exists(dir))
        {
            return [];
        }

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return [.. Directory.GetFiles(dir, fileGlob, option)];
    }
}
