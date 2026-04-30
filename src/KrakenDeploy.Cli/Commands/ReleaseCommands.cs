using System.CommandLine;

namespace KrakenDeploy.Cli.Commands;

/// <summary>
/// <c>kraken release create</c>  — snapshot process + pin package versions.<br/>
/// <c>kraken release list</c>    — list releases for a project.<br/>
/// <c>kraken release deploy</c>  — trigger a deployment, optionally waiting for completion.
/// </summary>
public static class ReleaseCommands
{
    public static Command Build(Option<string> serverOption, Option<string> apiKeyOption)
    {
        var releaseCommand = new Command("release", "Manage releases and deployments.");

        releaseCommand.AddCommand(BuildCreate(serverOption, apiKeyOption));
        releaseCommand.AddCommand(BuildList(serverOption, apiKeyOption));
        releaseCommand.AddCommand(BuildDeploy(serverOption, apiKeyOption));

        return releaseCommand;
    }

    // ── release create ────────────────────────────────────────────────────────

    private static Command BuildCreate(Option<string> serverOption, Option<string> apiKeyOption)
    {
        var projectOpt = new Option<string>("--project", "Project slug.") { IsRequired = true };
        var versionOpt = new Option<string>("--version", "Release version (e.g. 1.2.3).") { IsRequired = true };
        var notesOpt   = new Option<string?>("--notes", "Optional release notes.");
        var pkgOpt     = new Option<string[]>(
            "--package",
            "Pin a package version: <StepName>=<Version>. Repeatable.")
        {
            AllowMultipleArgumentsPerToken = false,
        };
        pkgOpt.Arity = ArgumentArity.ZeroOrMore;

        var cmd = new Command("create", "Create a new release for a project.");
        cmd.AddOption(projectOpt);
        cmd.AddOption(versionOpt);
        cmd.AddOption(notesOpt);
        cmd.AddOption(pkgOpt);

        cmd.SetHandler(async (project, version, notes, packages, server, apiKey) =>
        {
            using var client = new KrakenApiClient(server, apiKey);
            try
            {
                var proj = await client.GetProjectBySlugAsync(project).ConfigureAwait(false);
                if (proj is null)
                {
                    Console.Error.WriteLine($"Project '{project}' not found.");
                    return;
                }

                var pkgVersions = ParsePackageVersions(packages);
                var release = await client.CreateReleaseAsync(
                    proj.Id, version, notes, pkgVersions).ConfigureAwait(false);

                Console.WriteLine($"✓  Created release {release.Version} for {proj.Name} (id: {release.Id})");
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"Failed: {ex.Message}");
            }
            catch (Exception ex) when (ex.Message.Contains("already exists") || ex.Message.Contains("Conflict"))
            {
                Console.Error.WriteLine($"Release '{version}' already exists for project '{project}'.");
            }
        }, projectOpt, versionOpt, notesOpt, pkgOpt, serverOption, apiKeyOption);

        return cmd;
    }

    // ── release list ──────────────────────────────────────────────────────────

    private static Command BuildList(Option<string> serverOption, Option<string> apiKeyOption)
    {
        var projectOpt = new Option<string>("--project", "Project slug.") { IsRequired = true };

        var cmd = new Command("list", "List releases for a project.");
        cmd.AddOption(projectOpt);

        cmd.SetHandler(async (project, server, apiKey) =>
        {
            using var client = new KrakenApiClient(server, apiKey);
            try
            {
                var proj = await client.GetProjectBySlugAsync(project).ConfigureAwait(false);
                if (proj is null)
                {
                    Console.Error.WriteLine($"Project '{project}' not found.");
                    return;
                }

                var releases = await client.GetReleasesAsync(proj.Id).ConfigureAwait(false);
                if (releases.Count == 0)
                {
                    Console.WriteLine($"No releases found for '{project}'.");
                    return;
                }

                Console.WriteLine($"{"Version",-20}  {"Created",-26}  Id");
                Console.WriteLine(new string('-', 80));
                foreach (var r in releases)
                {
                    Console.WriteLine($"{r.Version,-20}  {r.CreatedUtc:yyyy-MM-dd HH:mm:ss}  {r.Id}");
                }
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"Failed: {ex.Message}");
            }
        }, projectOpt, serverOption, apiKeyOption);

        return cmd;
    }

    // ── release deploy ────────────────────────────────────────────────────────

    private static Command BuildDeploy(Option<string> serverOption, Option<string> apiKeyOption)
    {
        var projectOpt     = new Option<string>("--project",     "Project slug.")     { IsRequired = true };
        var releaseOpt     = new Option<string>("--release",     "Release version.") { IsRequired = true };
        var environmentOpt = new Option<string>("--environment", "Environment slug.") { IsRequired = true };
        var targetOpt      = new Option<string?>("--target", "Target name (optional; omit to let the server pick).");
        var waitOpt        = new Option<bool>("--wait", "Block until the deployment finishes and stream log output.");
        var timeoutOpt     = new Option<int>("--timeout", () => 600, "Timeout in seconds when --wait is set.");

        var cmd = new Command("deploy", "Trigger a deployment for a release.");
        cmd.AddOption(projectOpt);
        cmd.AddOption(releaseOpt);
        cmd.AddOption(environmentOpt);
        cmd.AddOption(targetOpt);
        cmd.AddOption(waitOpt);
        cmd.AddOption(timeoutOpt);

        cmd.SetHandler(async (project, release, environment, target, wait, timeout, server, apiKey) =>
        {
            using var client = new KrakenApiClient(server, apiKey);
            try
            {
                // Resolve project
                var proj = await client.GetProjectBySlugAsync(project).ConfigureAwait(false);
                if (proj is null) { Console.Error.WriteLine($"Project '{project}' not found."); return; }

                // Resolve release
                var releases = await client.GetReleasesAsync(proj.Id).ConfigureAwait(false);
                var rel = releases.FirstOrDefault(r =>
                    string.Equals(r.Version, release, StringComparison.OrdinalIgnoreCase));
                if (rel is null) { Console.Error.WriteLine($"Release '{release}' not found."); return; }

                // Resolve environment
                var envs = await client.GetEnvironmentsAsync().ConfigureAwait(false);
                var env = envs.FirstOrDefault(e =>
                    string.Equals(e.Slug, environment, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(e.Name, environment, StringComparison.OrdinalIgnoreCase));
                if (env is null) { Console.Error.WriteLine($"Environment '{environment}' not found."); return; }

                // Resolve target (optional)
                Guid? targetId = null;
                if (target is not null)
                {
                    var targets = await client.GetTargetsAsync().ConfigureAwait(false);
                    var tgt = targets.FirstOrDefault(t =>
                        string.Equals(t.Name, target, StringComparison.OrdinalIgnoreCase));
                    if (tgt is null) { Console.Error.WriteLine($"Target '{target}' not found."); return; }
                    targetId = tgt.Id;
                }

                var deployment = await client.CreateDeploymentAsync(rel.Id, env.Id, targetId)
                    .ConfigureAwait(false);

                Console.WriteLine($"✓  Deployment queued (id: {deployment.Id})");

                if (!wait)
                {
                    return;
                }

                var exitCode = await WaitForDeploymentAsync(client, deployment.Id, timeout)
                    .ConfigureAwait(false);

                Environment.ExitCode = exitCode;
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"Failed: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }, projectOpt, releaseOpt, environmentOpt, targetOpt, waitOpt, timeoutOpt,
           serverOption, apiKeyOption);

        return cmd;
    }

    // ── --wait polling ────────────────────────────────────────────────────────

    private static readonly HashSet<string> TerminalStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "Succeeded", "Failed", "Cancelled" };

    private static async Task<int> WaitForDeploymentAsync(
        KrakenApiClient client, Guid deploymentId, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        int nextSeq  = 0;

        Console.WriteLine("Waiting for deployment to complete…");

        while (DateTime.UtcNow < deadline)
        {
            // Fetch and print any new log lines.
            try
            {
                var logs = await client.GetDeploymentLogsAsync(deploymentId, nextSeq)
                    .ConfigureAwait(false);
                foreach (var entry in logs)
                {
                    var color = entry.Level switch
                    {
                        "error"   => ConsoleColor.Red,
                        "warning" => ConsoleColor.Yellow,
                        _         => ConsoleColor.White,
                    };
                    Console.ForegroundColor = color;
                    Console.WriteLine($"  [{entry.Timestamp:HH:mm:ss}] {entry.Message}");
                    Console.ResetColor();
                    nextSeq = Math.Max(nextSeq, entry.Sequence + 1);
                }
            }
            catch
            {
                // Swallow transient errors during polling.
            }

            // Check terminal status.
            var deployment = await client.GetDeploymentAsync(deploymentId).ConfigureAwait(false);
            if (deployment is not null && TerminalStatuses.Contains(deployment.Status))
            {
                Console.WriteLine($"\nDeployment {deployment.Status}.");
                return deployment.Status.Equals("Succeeded", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            }

            await Task.Delay(2_000).ConfigureAwait(false);
        }

        Console.Error.WriteLine($"\nTimeout: deployment did not finish within {timeoutSeconds}s.");
        return 2;
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static Dictionary<string, string>? ParsePackageVersions(string[] raw)
    {
        if (raw is null || raw.Length == 0)
        {
            return null;
        }
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in raw)
        {
            var eq = entry.IndexOf('=', StringComparison.Ordinal);
            if (eq < 1)
            {
                Console.Error.WriteLine($"Warning: ignoring malformed --package value '{entry}' (expected StepName=Version).");
                continue;
            }
            dict[entry[..eq]] = entry[(eq + 1)..];
        }
        return dict.Count > 0 ? dict : null;
    }
}
