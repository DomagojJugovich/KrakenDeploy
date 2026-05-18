using System.Xml;
using System.Xml.Linq;
using KrakenDeploy.Agent.Deployment.StepHandlers;
using Octostache;

namespace KrakenDeploy.Agent.Deployment.Package;

/// <summary>
/// Handles the <c>Octopus.TentaclePackage</c> step type — deploy the contents
/// of a package to a target, with Octopus feature passes applied to the
/// extracted package contents in order:
/// <list type="number">
///   <item><c>Octopus.Features.CustomDirectory</c> — copy to user-chosen path; optional pre-deploy purge with exclusions.</item>
///   <item><c>Octopus.Features.ConfigurationVariables</c> — XML <c>appSettings</c> / <c>connectionStrings</c> substitution against deployment variables.</item>
///   <item><c>Octopus.Features.ConfigurationTransforms</c> — XDT transforms (deferred — currently warns).</item>
/// </list>
/// <para>
/// Order matches Octopus's documented behaviour: CustomDirectory is a destination
/// move, ConfigurationVariables mutates the deployed XML, then transforms run last
/// so they can refine substituted values.
/// </para>
/// </summary>
public sealed class OctopusTentaclePackageStepHandler : IStepHandler
{
    public bool CanHandle(string stepType)
        => stepType.Equals("Octopus.TentaclePackage", StringComparison.OrdinalIgnoreCase);

    public bool RequiresPackage => true;

    public async Task<bool> HandleAsync(StepHandlerContext context, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(context.ExtractDir))
        {
            await context.LogAsync("error",
                "Octopus.TentaclePackage requires an extracted package but ExtractDir is empty.")
                .ConfigureAwait(false);
            return false;
        }

        var features = ParseFeatures(context.Step.Config);
        var octostache = BuildOctostache(context.Plan.Variables);
        var workingDir = context.ExtractDir;

        // 1. CustomDirectory
        if (features.Contains("Octopus.Features.CustomDirectory"))
        {
            var customDir = ResolveCustomDirectory(context.Step.Config, octostache);

            if (string.IsNullOrWhiteSpace(customDir))
            {
                await context.LogAsync("warning",
                    "Octopus.Features.CustomDirectory enabled but Octopus.Action.Package.CustomInstallationDirectory is empty — skipping copy.")
                    .ConfigureAwait(false);
            }
            else
            {
                var purge = ParseBool(context.Step.Config.GetValueOrDefault(
                    "Octopus.Action.Package.CustomInstallationDirectoryShouldBePurgedBeforeDeployment"));

                if (purge)
                {
                    var exclusions = ParseExclusions(
                        context.Step.Config.GetValueOrDefault(
                            "Octopus.Action.Package.CustomInstallationDirectoryPurgeExclusions"));
                    await PurgeDirectoryAsync(customDir, exclusions, context.LogAsync, ct).ConfigureAwait(false);
                }

                await context.LogAsync("info",
                    $"Copying package contents to '{customDir}'.").ConfigureAwait(false);

                CopyDirectory(context.ExtractDir, customDir);
                workingDir = customDir;
            }
        }
        else
        {
            await context.LogAsync("warning",
                "Octopus.Features.CustomDirectory is not enabled and KrakenDeploy has no Tentacle-managed application path — " +
                "the package was extracted to a staging directory but no install destination has been configured. " +
                "Set Octopus.Features.CustomDirectory + Octopus.Action.Package.CustomInstallationDirectory to deploy.")
                .ConfigureAwait(false);
        }

        // 2. ConfigurationVariables
        if (features.Contains("Octopus.Features.ConfigurationVariables") &&
            ParseBool(context.Step.Config.GetValueOrDefault(
                "Octopus.Action.Package.AutomaticallyUpdateAppSettingsAndConnectionStrings")))
        {
            await ApplyConfigurationVariablesAsync(
                workingDir, context.Plan.Variables, context.LogAsync, ct).ConfigureAwait(false);
        }

        // 3. ConfigurationTransforms (XDT) — not yet implemented
        if (features.Contains("Octopus.Features.ConfigurationTransforms") &&
            ParseBool(context.Step.Config.GetValueOrDefault(
                "Octopus.Action.Package.AutomaticallyRunConfigurationTransformationFiles")))
        {
            await context.LogAsync("warning",
                "Octopus.Features.ConfigurationTransforms is enabled but XDT support has not been implemented yet — " +
                "transformation files are NOT being applied. Track via Phase B-1 follow-up.")
                .ConfigureAwait(false);
        }

        return true;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static HashSet<string> ParseFeatures(IReadOnlyDictionary<string, string> config)
    {
        if (!config.TryGetValue("Octopus.Action.EnabledFeatures", out var raw)
            || string.IsNullOrWhiteSpace(raw))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        return new HashSet<string>(
            raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
    }

    private static bool ParseBool(string? value)
        => value is not null && value.Equals("True", StringComparison.OrdinalIgnoreCase);

    private static List<string> ParseExclusions(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }
        // Octopus accepts both newline- and comma-separated lists.
        return [.. raw.Split(
            ['\n', '\r', ','],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    private static string? ResolveCustomDirectory(
        IReadOnlyDictionary<string, string> config, VariableDictionary octostache)
    {
        if (!config.TryGetValue("Octopus.Action.Package.CustomInstallationDirectory", out var raw)
            || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        return octostache.Evaluate(raw);
    }

    private static VariableDictionary BuildOctostache(IReadOnlyDictionary<string, string> variables)
    {
        var dict = new VariableDictionary();
        foreach (var (k, v) in variables)
        {
            dict.Set(k, v);
        }
        return dict;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var srcFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, srcFile);
            var dst = Path.Combine(destination, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(srcFile, dst, overwrite: true);
        }
    }

    private static async Task PurgeDirectoryAsync(
        string directory,
        List<string> exclusions,
        Func<string, string, Task> log,
        CancellationToken ct)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        await log("info",
            $"Purging '{directory}'"
            + (exclusions.Count > 0 ? $" (excluding: {string.Join(", ", exclusions)})" : "")
            + ".").ConfigureAwait(false);

        // First-cut exclusion semantics: match against the top-level entry name only
        // (e.g. "App_Data" preserves the whole App_Data subtree). Full glob support
        // (e.g. "logs/*.txt") can be layered on later if a real export needs it.
        var exclusionSet = new HashSet<string>(exclusions, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            ct.ThrowIfCancellationRequested();
            if (exclusionSet.Contains(Path.GetFileName(entry)))
            {
                continue;
            }
            try
            {
                if (Directory.Exists(entry))
                {
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    File.Delete(entry);
                }
            }
            catch (Exception ex)
            {
                await log("warning",
                    $"Failed to delete '{entry}' during purge: {ex.Message}").ConfigureAwait(false);
            }
        }
    }

    private static async Task ApplyConfigurationVariablesAsync(
        string workingDir,
        IReadOnlyDictionary<string, string> variables,
        Func<string, string, Task> log,
        CancellationToken ct)
    {
        var configFiles = Directory
            .EnumerateFiles(workingDir, "*.config", SearchOption.AllDirectories)
            .Where(f => !IsXdtTransform(f))
            .ToList();

        if (configFiles.Count == 0)
        {
            await log("info", "ConfigurationVariables: no *.config files found.")
                .ConfigureAwait(false);
            return;
        }

        foreach (var file in configFiles)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var doc = XDocument.Load(file, LoadOptions.PreserveWhitespace);
                var modified = false;

                // appSettings / add[@key="X"] / @value
                foreach (var add in doc.Descendants("appSettings").SelectMany(s => s.Elements("add")))
                {
                    var key = add.Attribute("key")?.Value;
                    if (key is not null && variables.TryGetValue(key, out var newValue)
                        && add.Attribute("value")?.Value != newValue)
                    {
                        add.SetAttributeValue("value", newValue);
                        modified = true;
                    }
                }

                // connectionStrings / add[@name="X"] / @connectionString
                foreach (var add in doc.Descendants("connectionStrings").SelectMany(s => s.Elements("add")))
                {
                    var name = add.Attribute("name")?.Value;
                    if (name is not null && variables.TryGetValue(name, out var newValue)
                        && add.Attribute("connectionString")?.Value != newValue)
                    {
                        add.SetAttributeValue("connectionString", newValue);
                        modified = true;
                    }
                }

                if (modified)
                {
                    doc.Save(file, SaveOptions.DisableFormatting);
                    await log("info",
                        $"ConfigurationVariables: updated '{Path.GetRelativePath(workingDir, file)}'.")
                        .ConfigureAwait(false);
                }
            }
            catch (XmlException ex)
            {
                await log("warning",
                    $"ConfigurationVariables: skipped '{Path.GetRelativePath(workingDir, file)}' — invalid XML: {ex.Message}")
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A <c>*.config</c> file is treated as an XDT transform input — and therefore
    /// excluded from <c>ConfigurationVariables</c> substitution — when a sibling
    /// base file exists. Example: <c>Web.Production.config</c> is a transform when
    /// <c>Web.config</c> sits in the same directory.
    /// </summary>
    private static bool IsXdtTransform(string path)
    {
        var name = Path.GetFileName(path);
        var parts = name.Split('.');
        if (parts.Length < 3
            || !parts[^1].Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var baseName = string.Concat(string.Join('.', parts.Take(parts.Length - 2)), ".config");
        return File.Exists(Path.Combine(dir, baseName));
    }
}
