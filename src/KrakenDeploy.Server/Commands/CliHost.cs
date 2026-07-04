using KrakenDeploy.ControlPlane;
using KrakenDeploy.Server.Core.Domain.Accounts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace KrakenDeploy.Server.Commands;

/// <summary>
/// Shared helper for CLI command classes. Creates a <see cref="HostApplicationBuilder"/>
/// with the correct content root and defaults to the Development environment so
/// <c>appsettings.Development.json</c> is loaded.
/// </summary>
internal static class CliHost
{
    /// <summary>
    /// Creates a <see cref="HostApplicationBuilder"/> suitable for CLI admin commands.
    /// Uses the resolved content root and defaults to the Development environment
    /// (override with <c>DOTNET_ENVIRONMENT=Production</c>).
    /// </summary>
    public static HostApplicationBuilder CreateBuilder(string contentRoot)
    {
        var env = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? "Development";

        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = contentRoot,
            EnvironmentName = env,
        });

        // HostApplicationBuilder turns on ValidateOnBuild in the Development
        // environment, which eagerly validates *every* registered descriptor at
        // Build(). CLI commands register only a subset of the server graph
        // (AddKrakenDeployData + identity + encryption) and never start the web
        // host, so descriptors for web-only services (ILicenseGate, IKrakenAi)
        // and the cross-request cache singletons that capture a scoped
        // IDbContextFactory fail eager validation even though no CLI command
        // resolves them. WebApplication.CreateBuilder (the web host) leaves
        // ValidateOnBuild off for the same reason — mirror that here. ValidateScopes
        // stays on: CLI commands resolve everything inside a CreateAsyncScope, so
        // genuine scope misuse on the paths we actually exercise is still caught.
        builder.ConfigureContainer(new DefaultServiceProviderFactory(
            new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = false,
            }));

        return builder;
    }

    /// <summary>
    /// Resolves the database connection string a state-changing CLI command
    /// (<c>apikeys</c>, <c>users create-admin</c>, …) should operate against.
    /// <list type="bullet">
    /// <item>Single-instance: the configured <c>ConnectionStrings:KrakenDb</c>.</item>
    /// <item>Multi-account: requires <paramref name="account"/> (a tenant subdomain)
    /// and resolves that tenant's connection string via the control-plane catalog
    /// (mirrors <c>restore --account</c>). It <b>refuses</b> when no account is
    /// supplied — there is no single tenant DB in multi-account mode, and writing to
    /// the fallback <c>KrakenDb</c> would silently land in the wrong database
    /// (the row would never be read by the account-routed request path).</item>
    /// </list>
    /// Returns <c>null</c> after printing an actionable error when it cannot proceed;
    /// callers should treat null as exit code 1. The caller's <paramref name="builder"/>
    /// is only read from here (for config) — it is left un-built so the caller can
    /// register the data layer against the returned connection string.
    /// </summary>
    public static async Task<string?> ResolveTenantConnectionStringAsync(
        HostApplicationBuilder builder, string contentRoot, string? account)
    {
        if (!builder.Configuration.GetValue("MultiAccount:Enabled", false))
        {
            var cs = builder.Configuration.GetConnectionString("KrakenDb");
            if (string.IsNullOrWhiteSpace(cs))
            {
                Console.Error.WriteLine(
                    "ConnectionStrings:KrakenDb is not configured. " +
                    "Set it in appsettings.{Environment}.json or via user-secrets.");
                return null;
            }

            return cs;
        }

        // Multi-account: bind to the tenant resolved from --account.
        var resolved = await ResolveTenantAccountAsync(contentRoot, account).ConfigureAwait(false);
        return resolved?.ConnectionString;
    }

    /// <summary>
    /// Multi-account resolver primitive: maps <paramref name="account"/> (a tenant
    /// subdomain) to its <see cref="ResolvedAccount"/> (connection string + id +
    /// subdomain) via the control-plane catalog (mirrors <c>restore --account</c>).
    /// Callers that need more than the connection string — the account id for the
    /// per-tenant data slice, the subdomain for a backup manifest stamp — use this
    /// directly; <see cref="ResolveTenantConnectionStringAsync"/> wraps it for the
    /// common connection-string-only case. Returns <c>null</c> after printing an
    /// actionable error when the account is missing, the catalog is unconfigured, or
    /// the subdomain does not resolve to an active account. Call only when
    /// <c>MultiAccount:Enabled</c> is set.
    /// </summary>
    public static async Task<ResolvedAccount?> ResolveTenantAccountAsync(
        string contentRoot, string? account)
    {
        if (string.IsNullOrWhiteSpace(account))
        {
            Console.Error.WriteLine(
                "Multi-account mode is enabled: --account <subdomain> is required so the " +
                "operation targets the correct tenant database. There is no single KrakenDb " +
                "in multi-account mode, and a write to the fallback connection would silently " +
                "land in the wrong database.");
            return null;
        }

        // A throwaway control-plane host, used only to resolve the subdomain → tenant
        // account. The caller keeps its own builder for the data layer / dump work.
        var builder = CreateBuilder(contentRoot);

        var catalogConn = builder.Configuration.GetConnectionString("Catalog");
        if (string.IsNullOrWhiteSpace(catalogConn))
        {
            Console.Error.WriteLine("ConnectionStrings:Catalog is not configured (required for --account).");
            return null;
        }

        var baseDomain = builder.Configuration["MultiAccount:BaseDomain"] ?? "localhost";
        var dataPath = builder.Configuration["Server:DataPath"] ?? "data";

        builder.Services.AddKrakenControlPlane(builder.Configuration, catalogConn, dataPath);

        using var app = builder.Build();
        await using var scope = app.Services.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IAccountResolver>();
        var resolved = await resolver.ResolveAsync($"{account}.{baseDomain}").ConfigureAwait(false);
        if (resolved is null)
        {
            Console.Error.WriteLine($"Account '{account}' was not found or is not active in the catalog.");
            return null;
        }

        return resolved;
    }
}
