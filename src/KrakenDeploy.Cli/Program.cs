using System.CommandLine;
using KrakenDeploy.Cli.Commands;

// ── Global options ────────────────────────────────────────────────────────────

var serverOption = new Option<string>(
    ["--server", "-s"],
    () => Environment.GetEnvironmentVariable("KRAKEN_SERVER") ?? "http://localhost:5000",
    "KrakenDeploy server URL (env: KRAKEN_SERVER).");

var apiKeyOption = new Option<string>(
    ["--api-key", "-k"],
    () => Environment.GetEnvironmentVariable("KRAKEN_API_KEY") ?? string.Empty,
    "API key for authentication (env: KRAKEN_API_KEY).");

// ── Root command ──────────────────────────────────────────────────────────────

var root = new RootCommand("kraken — KrakenDeploy command-line interface");
root.AddGlobalOption(serverOption);
root.AddGlobalOption(apiKeyOption);

root.AddCommand(PackageCommands.Build(serverOption, apiKeyOption));
root.AddCommand(PackCommands.Build());
root.AddCommand(ReleaseCommands.Build(serverOption, apiKeyOption));
root.AddCommand(TargetCommands.Build(serverOption, apiKeyOption));

return await root.InvokeAsync(args).ConfigureAwait(false);
