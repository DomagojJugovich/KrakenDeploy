using Octostache;

namespace KrakenDeploy.Agent.Deployment.StepHandlers;

/// <summary>
/// Handles <c>Octopus.SubstituteVariables</c> step type.
/// <para>
/// Reads <c>Octopus.Action.SubstituteInFiles.TargetFiles</c> from the step config —
/// a newline- or comma-separated list of glob patterns (relative to <see cref="StepHandlerContext.ExtractDir"/>)
/// — and applies Octostache <c>#{VarName}</c> substitution to each matching file
/// using the resolved deployment variables.
/// </para>
/// </summary>
public sealed class SubstituteVariablesStepHandler : IStepHandler
{
    public bool CanHandle(string stepType)
        => stepType.Equals("Octopus.SubstituteVariables", StringComparison.OrdinalIgnoreCase);

    public bool RequiresPackage => true;

    public async Task<bool> HandleAsync(StepHandlerContext context, CancellationToken ct)
    {
        context.Step.Config.TryGetValue(
            "Octopus.Action.SubstituteInFiles.TargetFiles", out var targetFilesRaw);

        if (string.IsNullOrWhiteSpace(targetFilesRaw))
        {
            await context.LogAsync("warning",
                "No target files specified in Octopus.Action.SubstituteInFiles.TargetFiles — nothing to substitute.")
                .ConfigureAwait(false);
            return true;
        }

        // Build Octostache variable dictionary from resolved deployment variables.
        var varDict = new VariableDictionary();
        foreach (var (name, value) in context.Plan.Variables)
        {
            varDict.Set(name, value);
        }

        // Add array variables as comma-joined strings for basic compatibility.
        foreach (var (name, items) in context.Plan.ArrayVariables)
        {
            varDict.Set(name, string.Join(",", items));
        }

        // Add standard Octopus environment variables.
        varDict.Set("Octopus.Environment.Name", context.Plan.EnvironmentName);
        varDict.Set("Octopus.Deployment.Id", context.Plan.DeploymentId.ToString());

        // Resolve file patterns relative to the extract directory.
        var patterns = SplitPatterns(targetFilesRaw);
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
                    var original = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
                    var substituted = varDict.Evaluate(original);
                    await File.WriteAllTextAsync(filePath, substituted, ct).ConfigureAwait(false);

                    var rel = Path.GetRelativePath(context.ExtractDir, filePath);
                    await context.LogAsync("info", $"Substituted variables in '{rel}'.").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await context.LogAsync("error",
                        $"Failed to substitute variables in '{filePath}': {ex.Message}")
                        .ConfigureAwait(false);
                    allOk = false;
                }
            }
        }

        return allOk;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static List<string> SplitPatterns(string raw)
        => raw.Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries)
              .Select(p => p.Trim())
              .Where(p => !string.IsNullOrEmpty(p))
              .ToList();

    private static List<string> ResolveGlob(string baseDir, string pattern)
    {
        // Support simple * and **/* patterns via Directory.GetFiles.
        if (!pattern.Contains('*') && !pattern.Contains('?'))
        {
            var exact = Path.Combine(baseDir, pattern);
            return File.Exists(exact) ? [exact] : [];
        }

        var lastSlash  = pattern.LastIndexOfAny(['/', '\\']);
        var dir        = lastSlash >= 0 ? Path.Combine(baseDir, pattern[..lastSlash]) : baseDir;
        var fileGlob   = lastSlash >= 0 ? pattern[(lastSlash + 1)..] : pattern;
        var recursive  = pattern.Contains("**");

        if (!Directory.Exists(dir))
        {
            return [];
        }

        var option = recursive
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;

        return [.. Directory.GetFiles(dir, fileGlob, option)];
    }
}
