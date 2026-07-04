using System.Globalization;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KrakenDeploy.Server.Commands;

/// <summary>
/// <c>apikeys</c> CLI verbs (M13.C.4) — the bootstrap path for per-user API
/// keys on headless/scripted installs, mirroring <c>users create-admin</c>.
/// The static <c>ApiKey:Key</c> config value is gone; this is how the first
/// key gets minted before the UI is reachable.
/// </summary>
internal static class ApiKeyCommands
{
    public static async Task<int> RunAsync(string[] args, string contentRoot)
    {
        if (args.Length == 0)
        {
            return PrintTopLevelUsage();
        }

        return args[0] switch
        {
            "create" => await CreateAsync(args.AsSpan(1).ToArray(), contentRoot).ConfigureAwait(false),
            "list"   => await ListAsync(args.AsSpan(1).ToArray(), contentRoot).ConfigureAwait(false),
            "revoke" => await RevokeAsync(args.AsSpan(1).ToArray(), contentRoot).ConfigureAwait(false),
            "--help" or "-h" or "help" => PrintTopLevelUsage(success: true),
            _ => UnknownSubcommand(args[0]),
        };
    }

    private static async Task<int> CreateAsync(string[] args, string contentRoot)
    {
        string? user = null;
        string? name = null;
        string? space = null;
        string? account = null;
        int? expiresDays = 90; // mirror the UI default
        for (var i = 0; i < args.Length; i++)
        {
            var flag = args[i];
            // Value-taking flags must not be the last token — otherwise the
            // value is silently dropped (e.g. `--expires-days` last → falls
            // back to the 90-day default without warning).
            if (flag is "--user" or "--name" or "--space" or "--account" or "--expires-days"
                && i + 1 >= args.Length)
            {
                Console.Error.WriteLine($"{flag} requires a value.");
                return 1;
            }

            switch (flag)
            {
                case "--user":
                    user = args[++i];
                    break;
                case "--name":
                    name = args[++i];
                    break;
                case "--space":
                    space = args[++i];
                    break;
                case "--account":
                    account = args[++i];
                    break;
                case "--expires-days":
                    if (!int.TryParse(args[++i], NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out var days) || days <= 0)
                    {
                        Console.Error.WriteLine("--expires-days must be a positive integer.");
                        return 1;
                    }
                    expiresDays = days;
                    break;
                case "--no-expiry":
                    expiresDays = null;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(name))
        {
            Console.Error.WriteLine(
                "Usage: apikeys create --user <email-or-username> --name <purpose> " +
                "[--expires-days N | --no-expiry] [--space <slug>] [--account <subdomain>]");
            return 1;
        }

        var sp = await BuildProviderAsync(contentRoot, account).ConfigureAwait(false);
        if (sp is null) { return 1; }
        await using var _ = sp.ConfigureAwait(false);
        await using var scope = sp.CreateAsyncScope();
        var db = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<KrakenDbContext>>()
            .CreateDbContextAsync().ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var owner = await db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.UserName == user || u.Email == user)
                .ConfigureAwait(false);
            if (owner is null)
            {
                Console.Error.WriteLine($"No user found with username or email '{user}'.");
                return 1;
            }

            Guid? spaceId = null;
            if (!string.IsNullOrWhiteSpace(space))
            {
                var spaceRow = await db.Spaces.IgnoreQueryFilters().AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Slug == space).ConfigureAwait(false);
                if (spaceRow is null)
                {
                    Console.Error.WriteLine($"No Space found with slug '{space}'.");
                    return 1;
                }
                spaceId = spaceRow.Id;
            }

            var service = scope.ServiceProvider.GetRequiredService<ApiKeyService>();
            try
            {
                var created = await service.CreateAsync(
                    owner.Id,
                    name,
                    expiresDays is { } d ? DateTimeOffset.UtcNow.AddDays(d) : null,
                    spaceId).ConfigureAwait(false);

                Console.WriteLine($"API key created for '{owner.UserName}':");
                Console.WriteLine();
                Console.WriteLine($"  {created.PlainToken}");
                Console.WriteLine();
                Console.WriteLine("Copy it now — it is shown exactly once (only the hash is stored).");
                Console.WriteLine($"Name:    {created.Key.Name}");
                Console.WriteLine($"Hint:    {created.Key.Prefix}•••••••");
                Console.WriteLine($"Expires: {(created.Key.ExpiresUtc is { } e
                    ? e.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "never")}");
                Console.WriteLine($"Space:   {(space ?? "unrestricted")}");
                return 0;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }
    }

    private static async Task<int> ListAsync(string[] args, string contentRoot)
    {
        string? user = null;
        string? account = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--user" or "--account")
            {
                // Trailing value-taking flag with no value must NOT silently fall
                // through (e.g. `--user` last → listing every user's keys).
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine($"{args[i]} requires a value.");
                    return 1;
                }

                if (args[i] == "--user") { user = args[++i]; }
                else { account = args[++i]; }
            }
        }

        var sp = await BuildProviderAsync(contentRoot, account).ConfigureAwait(false);
        if (sp is null) { return 1; }
        await using var _ = sp.ConfigureAwait(false);
        await using var scope = sp.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ApiKeyService>();

        List<ApiKeyInfo> keys;
        if (string.IsNullOrWhiteSpace(user))
        {
            keys = await service.GetAllAsync().ConfigureAwait(false);
        }
        else
        {
            var db = await scope.ServiceProvider
                .GetRequiredService<IDbContextFactory<KrakenDbContext>>()
                .CreateDbContextAsync().ConfigureAwait(false);
            await using (db.ConfigureAwait(false))
            {
                var owner = await db.Users.AsNoTracking()
                    .FirstOrDefaultAsync(u => u.UserName == user || u.Email == user)
                    .ConfigureAwait(false);
                if (owner is null)
                {
                    Console.Error.WriteLine($"No user found with username or email '{user}'.");
                    return 1;
                }
                keys = await service.GetForUserAsync(owner.Id).ConfigureAwait(false);
            }
        }

        if (keys.Count == 0)
        {
            Console.WriteLine("No API keys found.");
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        Console.WriteLine($"{"Id",-38} {"Owner",-24} {"Name",-24} {"Hint",-20} {"Status",-8} Expires");
        foreach (var k in keys)
        {
            var status = k.IsRevoked ? "Revoked" : k.IsExpired(now) ? "Expired" : "Active";
            var expires = k.ExpiresUtc?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "never";
            Console.WriteLine($"{k.Id,-38} {k.UserName,-24} {k.Name,-24} {k.Hint,-20} {status,-8} {expires}");
        }
        return 0;
    }

    private static async Task<int> RevokeAsync(string[] args, string contentRoot)
    {
        Guid? id = null;
        string? account = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--id")
            {
                if (i + 1 >= args.Length || !Guid.TryParse(args[++i], out var parsed))
                {
                    Console.Error.WriteLine("--id requires a valid GUID value.");
                    return 1;
                }
                id = parsed;
            }
            else if (args[i] == "--account")
            {
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine("--account requires a value.");
                    return 1;
                }
                account = args[++i];
            }
        }

        if (id is null)
        {
            Console.Error.WriteLine(
                "Usage: apikeys revoke --id <guid> [--account <subdomain>]   (ids from: apikeys list)");
            return 1;
        }

        var sp = await BuildProviderAsync(contentRoot, account).ConfigureAwait(false);
        if (sp is null) { return 1; }
        await using var _ = sp.ConfigureAwait(false);
        await using var scope = sp.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ApiKeyService>();

        if (!await service.RevokeAsync(id.Value).ConfigureAwait(false))
        {
            Console.Error.WriteLine($"No API key with id '{id}'.");
            return 1;
        }

        Console.WriteLine($"API key {id} revoked — it no longer authenticates.");
        return 0;
    }

    /// <summary>
    /// Builds the CLI service provider bound to the correct database, or returns
    /// null (after printing an error) when the operation cannot run. Single-instance
    /// binds to the configured <c>KrakenDb</c>; multi-account requires
    /// <c>--account &lt;subdomain&gt;</c> and binds to that tenant's database resolved
    /// via the catalog (see <see cref="CliHost.ResolveTenantConnectionStringAsync"/>).
    /// Feeding the resolved tenant connection string into <c>AddKrakenDeployData</c>
    /// is safe: the CLI's default <c>DisabledAccountContext</c> makes
    /// <c>KrakenDbContext.OnConfiguring</c> a no-op, so the fixed connection is used
    /// verbatim and the key is written to — and later read from — the right tenant DB.
    /// </summary>
    private static async Task<ServiceProvider?> BuildProviderAsync(string contentRoot, string? account)
    {
        var builder = CliHost.CreateBuilder(contentRoot);

        var connectionString = await CliHost
            .ResolveTenantConnectionStringAsync(builder, contentRoot, account)
            .ConfigureAwait(false);
        if (connectionString is null)
        {
            return null; // the resolver already printed the reason
        }

        builder.Services.AddKrakenDeployData(connectionString);
        // Match UserCommands: no migration here — commands assume the schema
        // exists (run `database migrate` / boot the server first).
        return builder.Services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });
    }

    private static int PrintTopLevelUsage(bool success = false)
    {
        Console.WriteLine("Usage: apikeys <create|list|revoke> [options]");
        Console.WriteLine();
        Console.WriteLine("  create --user <email-or-username> --name <purpose>");
        Console.WriteLine("         [--expires-days N | --no-expiry]  (default: 90 days)");
        Console.WriteLine("         [--space <slug>]                  (restrict to one Space)");
        Console.WriteLine("  list   [--user <email-or-username>]");
        Console.WriteLine("  revoke --id <guid>");
        Console.WriteLine();
        Console.WriteLine("  --account <subdomain>   (required in multi-account mode: selects the");
        Console.WriteLine("                           tenant database; ignored single-instance)");
        return success ? 0 : 1;
    }

    private static int UnknownSubcommand(string sub)
    {
        Console.Error.WriteLine($"Unknown apikeys subcommand '{sub}'.");
        PrintTopLevelUsage();
        return 1;
    }
}
