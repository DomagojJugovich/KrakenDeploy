using System.Text.Json;
using KrakenDeploy.Server.Core.Domain.Ai;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Security;
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
    /// in the middleware pipeline AFTER <c>UseAuthentication</c>/
    /// <c>UseAuthorization</c> (and, in multi-account, after
    /// <c>AccountResolutionMiddleware</c>) so the gate can resolve the calling
    /// account + the API key's bound Space to enforce the per-Space
    /// MCP-enabled flag.
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
/// The calling Space is the API key's bound Space when the key carries a
/// Space restriction (M13.C.4), else the Default Space. The middleware runs
/// BEFORE endpoint authorization, so the ApiKey principal is not on
/// <c>HttpContext.User</c> yet — it triggers the scheme explicitly via
/// <c>AuthenticateAsync</c> (the result is request-cached by the framework,
/// so the later policy evaluation reuses it — no double DB hit). Per-Space
/// results sit in a 30-second cache to keep the hot path cheap.
/// </para>
/// </summary>
public sealed class McpEnabledGateMiddleware(
    RequestDelegate next,
    IServiceScopeFactory scopeFactory,
    ILogger<McpEnabledGateMiddleware> logger)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    // Keyed by (account, space) — NOT space alone. In multi-account every
    // tenant DB shares the same WellKnown.DefaultSpaceId constant, so a
    // space-only key would serve account A's McpEnabled flag to account B
    // (cross-tenant poisoning). The account is Guid.Empty in single-instance.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        (Guid Account, Guid Space), (bool Enabled, DateTimeOffset RefreshedUtc)> Cache = new();

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var spaceId = await ResolveCallingSpaceAsync(context).ConfigureAwait(false);
        var enabled = await ResolveMcpEnabledAsync(spaceId, context.RequestAborted).ConfigureAwait(false);
        if (!enabled)
        {
            logger.LogWarning(
                "MCP request rejected: McpEnabled flag is OFF for Space {SpaceId}. " +
                "Toggle it on at /configuration/ai-settings to allow MCP traffic.",
                spaceId);
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

    /// <summary>The bound Space of a restricted API key, else Default.</summary>
    private static async Task<Guid> ResolveCallingSpaceAsync(HttpContext context)
    {
        // Bare-HttpContext harnesses (and any pipeline without authentication
        // wired) have no IAuthenticationService — treat as an unrestricted
        // caller rather than throwing. In production the scheme always exists.
        if (context.RequestServices?.GetService<
                Microsoft.AspNetCore.Authentication.IAuthenticationService>() is null)
        {
            return WellKnown.DefaultSpaceId;
        }

        var auth = await Microsoft.AspNetCore.Authentication.AuthenticationHttpContextExtensions
            .AuthenticateAsync(context, KrakenAuthSchemes.ApiKey).ConfigureAwait(false);
        var claim = auth.Succeeded
            ? auth.Principal.FindFirst(KrakenClaimTypes.ApiKeySpace)?.Value
            : null;
        return claim is not null && Guid.TryParse(claim, out var spaceId)
            ? spaceId
            : WellKnown.DefaultSpaceId;
    }

    private async Task<bool> ResolveMcpEnabledAsync(Guid spaceId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();

        // Compound the cache key with the active account so the shared
        // DefaultSpaceId can't leak one tenant's flag to another. IsResolved
        // never throws (unlike CurrentAccountId); Guid.Empty in single-instance.
        var accountCtx = scope.ServiceProvider
            .GetService<KrakenDeploy.Server.Core.Domain.Accounts.IAccountContext>();
        var accountId = accountCtx?.IsResolved == true ? accountCtx.CurrentAccountId : Guid.Empty;
        var key = (accountId, spaceId);

        if (Cache.TryGetValue(key, out var cached)
            && DateTimeOffset.UtcNow - cached.RefreshedUtc < CacheTtl)
        {
            return cached.Enabled;
        }

        // Read the Space's AI settings document by explicit Space id. The
        // `settings` table is not ISpaceScoped, so SettingsService cages by the
        // passed id (no query filter to bypass); a missing document yields a
        // default (McpEnabled = false) — the gate stays fail-closed.
        var settings = scope.ServiceProvider
            .GetRequiredService<KrakenDeploy.Server.Data.Services.SettingsService>();
        var doc = await settings
            .GetAsync<KrakenDeploy.Server.Core.Domain.Ai.SpaceAiSettings>(spaceId, ct)
            .ConfigureAwait(false);
        var enabled = doc.McpEnabled;

        Cache[key] = (enabled, DateTimeOffset.UtcNow);
        return enabled;
    }

    /// <summary>Test-only: clear the in-memory enable-flag cache so a
    /// flag-toggle in a test takes effect immediately.</summary>
    internal static void ClearCacheForTest() => Cache.Clear();
}
