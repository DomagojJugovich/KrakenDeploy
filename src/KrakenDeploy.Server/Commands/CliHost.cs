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

        return new HostApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = contentRoot,
            EnvironmentName = env,
        });
    }
}
