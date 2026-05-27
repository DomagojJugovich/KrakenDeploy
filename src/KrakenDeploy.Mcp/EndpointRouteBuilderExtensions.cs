using System.Text.Json;
using KrakenDeploy.Server.Core.Domain.Ai;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Mcp;

/// <summary>
/// Endpoint extensions that mount the MCP server on the Kraken ASP.NET
/// pipeline (M11.B).
/// </summary>
public static class EndpointRouteBuilderExtensions
{
    /// <summary>The default mount path for the MCP server.</summary>
    public const string DefaultPath = "/mcp";

    /// <summary>
    /// Maps the MCP Streamable HTTP transport at <paramref name="path"/>
    /// (default <c>/mcp</c>) with API-key auth attached. Pair with
    /// <see cref="ApplicationBuilderExtensions.UseKrakenMcpEnabledGate"/>
    /// in the middleware pipeline before <c>UseRouting</c> to enforce the
    /// per-Space MCP-enabled flag.
    /// <para>
    /// The split (gate in middleware, endpoint in routing) exists because
    /// <see cref="ModelContextProtocol.AspNetCore.McpEndpointRouteBuilderExtensions.MapMcp"/>
    /// returns the generic <see cref="IEndpointConventionBuilder"/> which
    /// doesn't expose <c>AddEndpointFilter</c> — that's minimal-API only.
    /// Middleware scoped via <see cref="UseWhenExtensions.UseWhen"/> gives
    /// us the same "only on /mcp" scope without the filter machinery.
    /// </para>
    /// </summary>
    public static IEndpointConventionBuilder MapKrakenMcp(
        this IEndpointRouteBuilder endpoints, string path = DefaultPath)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return endpoints.MapMcp(path).RequireAuthorization();
    }
}

/// <summary>
/// Middleware-side companion to <see cref="EndpointRouteBuilderExtensions.MapKrakenMcp"/>
/// that enforces the per-Space <see cref="SpaceAiSettings.McpEnabled"/>
/// flag before requests reach the MCP transport. Wire from
/// <c>Program.cs</c> with <see cref="UseKrakenMcpEnabledGate"/>.
/// </summary>
public static class ApplicationBuilderExtensions
{
    /// <summary>
    /// Adds the per-Space MCP-enabled gate to the pipeline, scoped to the
    /// MCP path (default <c>/mcp</c>). Short-circuits with 403 + a JSON
    /// error body when <see cref="SpaceAiSettings.McpEnabled"/> is off
    /// for the calling Space.
    /// </summary>
    public static IApplicationBuilder UseKrakenMcpEnabledGate(
        this IApplicationBuilder app, string path = EndpointRouteBuilderExtensions.DefaultPath)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return app.UseWhen(
            ctx => ctx.Request.Path.StartsWithSegments(path),
            branch => branch.UseMiddleware<McpEnabledGateMiddleware>());
    }
}

/// <summary>
/// Per-Space gate on the MCP-enabled flag. Resolves
/// <see cref="SpaceAiSettings.McpEnabled"/> for the calling Space and
/// short-circuits with 403 when the flag is off.
/// <para>
/// <strong>v1 simplification</strong>: the current
/// <c>ApiKeyAuthenticationHandler</c> issues a single shared CLI-style
/// principal without a Space claim, so the gate always reads the Default
/// Space's settings. When M13.C.4 introduces per-user API keys with Space
/// scope, this middleware will switch to the API key's bound Space —
/// same gate, different lookup. The 30-second DB-row cache keeps the hot
/// path cheap even on busy MCP sessions.
/// </para>
/// </summary>
public sealed class McpEnabledGateMiddleware(
    RequestDelegate next,
    IDbContextFactory<KrakenDbContext> dbFactory,
    ILogger<McpEnabledGateMiddleware> logger)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private static readonly Lock Gate = new();
    private static (bool Enabled, DateTimeOffset RefreshedUtc)? _cached;

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var enabled = await ResolveMcpEnabledAsync(context.RequestAborted).ConfigureAwait(false);
        if (!enabled)
        {
            logger.LogWarning(
                "MCP request rejected: per-Space McpEnabled flag is OFF. " +
                "Toggle it on at /configuration/ai-settings to allow MCP traffic.");
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            var body = JsonSerializer.Serialize(new
            {
                error = "MCP server is disabled for this Space. " +
                        "Toggle 'MCP' on at /configuration/ai-settings to enable.",
            });
            await context.Response.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
            return;
        }
        await next(context).ConfigureAwait(false);
    }

    private async Task<bool> ResolveMcpEnabledAsync(CancellationToken ct)
    {
        lock (Gate)
        {
            if (_cached is { } cached
                && DateTimeOffset.UtcNow - cached.RefreshedUtc < CacheTtl)
            {
                return cached.Enabled;
            }
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var enabled = await db.SpaceAiSettings
            .IgnoreQueryFilters() // the gate looks across the SpaceScopingInterceptor
            .Where(s => s.SpaceId == WellKnown.DefaultSpaceId)
            .Select(s => s.McpEnabled)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        lock (Gate)
        {
            _cached = (enabled, DateTimeOffset.UtcNow);
        }
        return enabled;
    }

    /// <summary>Test-only: clear the in-memory enable-flag cache so a
    /// flag-toggle in a test takes effect immediately.</summary>
    internal static void ClearCacheForTest()
    {
        lock (Gate) { _cached = null; }
    }
}
