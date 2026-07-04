using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace KrakenDeploy.Mcp;

/// <summary>
/// DI registration for the KrakenDeploy MCP server (M11.B).
/// <para>
/// Wires <c>AddMcpServer()</c> with HTTP transport + assembly-scanned tools
/// and resources discovered via the SDK's
/// <c>[McpServerToolType]</c> / <c>[McpServerResourceType]</c> attributes.
/// </para>
/// <para>
/// <strong>Wire model:</strong> in-process server, hosted inside the main
/// <c>KrakenDeploy.Server</c> ASP.NET pipeline. The endpoint is mapped at
/// <c>/mcp</c> via <see cref="EndpointRouteBuilderExtensions.MapKrakenMcp"/>.
/// All MCP calls reuse the <c>ApiKey</c> authentication scheme (the same
/// <c>X-Api-Key</c> header the CLI uses) carrying a per-user key (M13.C.4)
/// — the caller authenticates AS the key's owning user and mutating tools
/// gate on the owner's real permissions. Per-Space <c>McpEnabled</c> gating
/// on <c>SpaceAiSettings</c> is applied at the endpoint level (keyed by a
/// restricted key's bound Space) — see
/// <see cref="EndpointRouteBuilderExtensions"/>.
/// </para>
/// <para>
/// <strong>Tools + Resources discovery:</strong> the
/// <c>WithToolsFromAssembly</c> / <c>WithResourcesFromAssembly</c> calls
/// scan this assembly for types decorated with the SDK's
/// <c>[McpServerToolType]</c> / <c>[McpServerResourceType]</c> attributes.
/// Commit 1 (this file's commit) ships the skeleton with no tools or
/// resources defined; subsequent commits add them and they get picked up
/// automatically.
/// </para>
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Kraken MCP server with the DI container. Call from
    /// <c>Program.cs</c> alongside the other <c>AddKrakenDeploy*</c>
    /// extensions. Pair with
    /// <see cref="EndpointRouteBuilderExtensions.MapKrakenMcp"/> on the
    /// <c>WebApplication</c>.
    /// </summary>
    public static IServiceCollection AddKrakenMcp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var mcpAssembly = typeof(ServiceCollectionExtensions).Assembly;
        var serverInfo = new ModelContextProtocol.Protocol.Implementation
        {
            Name    = "kraken-deploy",
            Version = ResolveAssemblyVersion(mcpAssembly),
        };

        services.AddMcpServer(opts => opts.ServerInfo = serverInfo)
            .WithHttpTransport()
            .WithToolsFromAssembly(mcpAssembly)
            .WithResourcesFromAssembly(mcpAssembly);

        return services;
    }

    private static string ResolveAssemblyVersion(Assembly assembly)
    {
        // InformationalVersion is what `dotnet --version`-style consumers
        // expect (carries pre-release suffixes from CI builds). Falls back
        // to the assembly version if the attribute is absent — never null,
        // never crashes if the build didn't stamp metadata.
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            return info;
        }
        return assembly.GetName().Version?.ToString() ?? "0.0.0";
    }
}
