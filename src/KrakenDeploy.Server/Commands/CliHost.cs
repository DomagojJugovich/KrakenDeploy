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
}
