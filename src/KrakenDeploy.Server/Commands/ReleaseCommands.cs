using System.Globalization;
using KrakenDeploy.ControlPlane;
using KrakenDeploy.Platform;
using KrakenDeploy.Platform.Releases;
using KrakenDeploy.Server.Core.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KrakenDeploy.Server.Commands;

/// <summary>
/// CLI subcommand <c>releases</c> — deploy orchestration for the blue-green slot
/// scheme (docs/blue-green-slot-deployment.md §5/§8):
/// <code>
/// releases register --id 2026.07.02-a1b2c3 --label "v1.4.0" --slot 2
/// releases flip     --id 2026.07.02-a1b2c3 [--drain-window-hours 24]
/// releases retire   --id 2026.06.20-9f8e7d
/// releases status
/// </code>
/// Available under the blue-green topologies (BG1/T1 — supersedes D-bg-5):
/// <c>OnPremBlueGreen</c> keeps the registry in KrakenDb (<c>platform</c> schema);
/// <c>Saas</c> keeps it in the control-plane catalog. Refused under <c>OnPrem</c>
/// (no slots, no router — upgrades are stop → migrate → start). After a
/// flip/retire the routers converge within their cache TTL; configured
/// <c>Releases:RouterInvalidateUrls</c> are POSTed best-effort to converge
/// immediately.
/// </summary>
internal static class ReleaseCommands
{
    public static async Task<int> RunAsync(string[] args, string contentRoot)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var verb = args[0];
        string? id = null;
        double? drainWindowHours = null;
        string? label = null;
        short? slot = null;

        for (var i = 1; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--id": id = args[i + 1]; break;
                case "--label": label = args[i + 1]; break;
                case "--slot":
                    if (short.TryParse(args[i + 1], CultureInfo.InvariantCulture, out var s)) { slot = s; }
                    break;
                case "--drain-window-hours":
                    if (double.TryParse(args[i + 1], CultureInfo.InvariantCulture, out var h)) { drainWindowHours = h; }
                    break;
            }
        }

        var builder = CliHost.CreateBuilder(contentRoot);

        var topology = CliHost.ResolveTopologyOrError(builder.Configuration);
        if (topology is null)
        {
            return 1;
        }

        if (topology == DeploymentTopology.OnPrem)
        {
            Console.Error.WriteLine(
                "The blue-green release registry needs Deployment:Topology=OnPremBlueGreen or Saas " +
                "(BG1/T1). Topology=OnPrem installs upgrade via stop → migrate → start.");
            return 1;
        }

        if (topology == DeploymentTopology.Saas)
        {
            // Saas: registry lives in the control-plane catalog (public schema).
            var catalogConn = builder.Configuration.GetConnectionString("Catalog");
            if (string.IsNullOrWhiteSpace(catalogConn))
            {
                Console.Error.WriteLine("ConnectionStrings:Catalog is not configured.");
                return 1;
            }

            var dataPath = builder.Configuration["Server:DataPath"] ?? "data";
            builder.Services.AddKrakenControlPlane(builder.Configuration, catalogConn, dataPath);
        }
        else
        {
            // OnPremBlueGreen: registry lives in KrakenDb under the `platform` schema.
            var krakenConn = builder.Configuration.GetConnectionString("KrakenDb");
            if (string.IsNullOrWhiteSpace(krakenConn))
            {
                Console.Error.WriteLine("ConnectionStrings:KrakenDb is not configured.");
                return 1;
            }

            builder.Services.AddPlatformReleaseRegistry(krakenConn, ownSchema: true);
        }

        using var app = builder.Build();
        await using (var scope0 = app.Services.CreateAsyncScope())
        {
            // Make sure the registry tables exist so the first `releases` command on
            // a fresh install just works. Saas: the catalog migration chain owns them
            // and stays additive. OnPremBlueGreen: the platform chain (own history
            // table) is infrastructure that never changes with app releases, so
            // applying it here is always safe.
            if (topology == DeploymentTopology.Saas)
            {
                var catalogFactory = scope0.ServiceProvider
                    .GetRequiredService<IDbContextFactory<KrakenDeploy.ControlPlane.Catalog.CatalogDbContext>>();
                await using var catalog = await catalogFactory.CreateDbContextAsync().ConfigureAwait(false);
                await catalog.Database.MigrateAsync().ConfigureAwait(false);
            }
            else
            {
                var platformFactory = scope0.ServiceProvider
                    .GetRequiredService<IDbContextFactory<PlatformReleaseDbContext>>();
                await using var platform = await platformFactory.CreateDbContextAsync().ConfigureAwait(false);
                await platform.Database.MigrateAsync().ConfigureAwait(false);
            }
        }

        await using var scope = app.Services.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<ReleaseRegistry>();

        try
        {
            switch (verb)
            {
                case "register":
                {
                    if (id is null || label is null || slot is null)
                    {
                        Console.Error.WriteLine(
                            "Usage: releases register --id <release-id> --label <label> --slot <n>");
                        return 1;
                    }

                    await registry.RegisterAsync(id, label, slot.Value).ConfigureAwait(false);
                    Console.WriteLine(
                        $"Registered '{id}' ({label}) into slot {slot} as Deploying. " +
                        $"Health-gate it through the router with the X-KD-Release header, then `releases flip --id {id}`.");
                    return 0;
                }

                case "flip":
                {
                    if (id is null)
                    {
                        Console.Error.WriteLine("Usage: releases flip --id <release-id> [--drain-window-hours <h>]");
                        return 1;
                    }

                    var hours = drainWindowHours
                        ?? builder.Configuration.GetValue("Releases:DrainWindowHours", 24.0);
                    await registry.FlipDefaultAsync(id, TimeSpan.FromHours(hours)).ConfigureAwait(false);
                    Console.WriteLine(
                        $"Default release is now '{id}'. Previous default (if any) is Draining " +
                        $"(deadline in {hours:0.#} h). New sessions/agents land on '{id}'.");
                    await InvalidateRoutersAsync(builder.Configuration).ConfigureAwait(false);
                    return 0;
                }

                case "retire":
                {
                    if (id is null)
                    {
                        Console.Error.WriteLine("Usage: releases retire --id <release-id>");
                        return 1;
                    }

                    await registry.RetireAsync(id).ConfigureAwait(false);
                    Console.WriteLine($"Release '{id}' is Retired; its slot is free for the next deploy.");
                    // §9: retiring is only safe at zero circuits + zero in-flight
                    // deployments. The drain-watcher verifies that via /slot-metrics;
                    // this manual path cannot, so say so.
                    Console.WriteLine(
                        "NOTE: manual retire does NOT verify the slot is empty. Prefer letting the " +
                        "drain-watcher retire it (kraken.release-drain-watch), or check each slot " +
                        "instance's /slot-metrics first — redeploying into a non-empty slot " +
                        "force-kills its circuits and in-flight deployments.");
                    await InvalidateRoutersAsync(builder.Configuration).ConfigureAwait(false);
                    return 0;
                }

                case "status":
                {
                    var snapshot = await registry.GetSnapshotAsync().ConfigureAwait(false);
                    Console.WriteLine($"current_default_release: {snapshot.DefaultReleaseId ?? "<unset>"}");
                    Console.WriteLine();
                    Console.WriteLine($"{"RELEASE",-28} {"LABEL",-20} {"SLOT",4}  {"STATUS",-9} {"DEPLOYED (UTC)",-17} DRAIN DEADLINE (UTC)");
                    foreach (var r in snapshot.Releases)
                    {
                        var marker = r.Id == snapshot.DefaultReleaseId ? "*" : " ";
                        Console.WriteLine(
                            $"{marker}{r.Id,-27} {r.Label,-20} {r.SlotNo,4}  {r.Status,-9} " +
                            $"{r.DeployedAtUtc:yyyy-MM-dd HH:mm}   {r.DrainDeadlineUtc?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "-"}");
                    }

                    if (snapshot.Releases.Count == 0)
                    {
                        Console.WriteLine("(no releases registered)");
                    }

                    return 0;
                }

                default:
                    PrintUsage();
                    return 1;
            }
        }
        catch (InvalidOperationException ex)
        {
            // Registry invariant violations (occupied slot, retiring the default, …)
            // are operator errors with actionable messages — print, don't stack-trace.
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Best-effort push-invalidation of each node router's snapshot cache
    /// (<c>Releases:RouterInvalidateUrls</c>, e.g. <c>http://app-node-1:8080</c>).
    /// Failures are reported but never fail the command — routers converge within
    /// their cache TTL anyway.
    /// </summary>
    private static async Task InvalidateRoutersAsync(ConfigurationManager configuration)
    {
        var urls = configuration.GetSection("Releases:RouterInvalidateUrls").Get<string[]>() ?? [];
        if (urls.Length == 0)
        {
            return;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        // The router requires its ops token (Router:OpsToken) on this endpoint.
        var opsToken = configuration["Releases:RouterOpsToken"];
        if (!string.IsNullOrWhiteSpace(opsToken))
        {
            http.DefaultRequestHeaders.Add("X-KD-Ops-Token", opsToken);
        }

        foreach (var url in urls)
        {
            try
            {
                using var response = await http
                    .PostAsync(new Uri(new Uri(url), "/kd-router/invalidate"), content: null)
                    .ConfigureAwait(false);
                Console.WriteLine($"  router {url}: cache invalidated ({(int)response.StatusCode}).");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
            {
                Console.WriteLine($"  router {url}: invalidate failed ({ex.Message}) — will converge via TTL.");
            }
        }
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  releases register --id <release-id> --label <label> --slot <n>");
        Console.Error.WriteLine("  releases flip     --id <release-id> [--drain-window-hours <h>]");
        Console.Error.WriteLine("  releases retire   --id <release-id>");
        Console.Error.WriteLine("  releases status");
    }
}
