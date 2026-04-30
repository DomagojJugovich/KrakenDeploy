using System.CommandLine;

namespace KrakenDeploy.Cli.Commands;

/// <summary>
/// <c>kraken target list</c>   — list all registered targets.<br/>
/// <c>kraken target health</c> — show online/offline status for each target.
/// </summary>
public static class TargetCommands
{
    public static Command Build(Option<string> serverOption, Option<string> apiKeyOption)
    {
        var targetCommand = new Command("target", "Inspect deployment targets.");

        targetCommand.AddCommand(BuildList(serverOption, apiKeyOption));
        targetCommand.AddCommand(BuildHealth(serverOption, apiKeyOption));

        return targetCommand;
    }

    // ── target list ───────────────────────────────────────────────────────────

    private static Command BuildList(Option<string> serverOption, Option<string> apiKeyOption)
    {
        var cmd = new Command("list", "List all registered deployment targets.");

        cmd.SetHandler(async (server, apiKey) =>
        {
            using var client = new KrakenApiClient(server, apiKey);
            try
            {
                var targets = await client.GetTargetsAsync().ConfigureAwait(false);
                if (targets.Count == 0)
                {
                    Console.WriteLine("No targets registered.");
                    return;
                }

                Console.WriteLine($"{"Name",-24}  {"Status",-12}  {"Machine",-20}  Roles");
                Console.WriteLine(new string('-', 80));
                foreach (var t in targets)
                {
                    var roles   = string.Join(", ", t.Roles);
                    var machine = t.MachineName ?? "-";
                    Console.WriteLine($"{t.Name,-24}  {t.Status,-12}  {machine,-20}  {roles}");
                }
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"Failed: {ex.Message}");
            }
        }, serverOption, apiKeyOption);

        return cmd;
    }

    // ── target health ─────────────────────────────────────────────────────────

    private static Command BuildHealth(Option<string> serverOption, Option<string> apiKeyOption)
    {
        var cmd = new Command("health", "Show connection health for all targets.");

        cmd.SetHandler(async (server, apiKey) =>
        {
            using var client = new KrakenApiClient(server, apiKey);
            try
            {
                var targets = await client.GetTargetsAsync().ConfigureAwait(false);
                if (targets.Count == 0)
                {
                    Console.WriteLine("No targets registered.");
                    return;
                }

                var now = DateTimeOffset.UtcNow;

                Console.WriteLine($"{"Name",-24}  {"Status",-12}  {"Last Seen",-26}  {"OS",-16}  Agent Ver");
                Console.WriteLine(new string('-', 100));

                foreach (var t in targets)
                {
                    var statusColor = t.Status switch
                    {
                        "Online"  => ConsoleColor.Green,
                        "Offline" => ConsoleColor.Red,
                        _         => ConsoleColor.Yellow,
                    };

                    var lastSeen = t.LastSeenUtc.HasValue
                        ? $"{(now - t.LastSeenUtc.Value).TotalSeconds:F0}s ago"
                        : "never";

                    Console.ForegroundColor = statusColor;
                    Console.Write($"{t.Name,-24}  {t.Status,-12}");
                    Console.ResetColor();
                    Console.WriteLine(
                        $"  {lastSeen,-26}  {t.OperatingSystem ?? "-",-16}  {t.AgentVersion ?? "-"}");
                }
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"Failed: {ex.Message}");
            }
        }, serverOption, apiKeyOption);

        return cmd;
    }
}
