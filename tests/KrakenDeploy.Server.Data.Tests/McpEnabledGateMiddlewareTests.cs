using FluentAssertions;
using KrakenDeploy.Mcp;
using KrakenDeploy.Server.Core.Domain.Ai;
using KrakenDeploy.Server.Core.Domain.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// M11.B Commit 1 — pins the per-Space MCP-enabled gate. The gate is the
/// only Kraken-authored logic in the skeleton (the protocol handshake is
/// the SDK's responsibility), so it's the piece worth a test before
/// Resources / Tools land. Exercises the middleware directly with a
/// <see cref="DefaultHttpContext"/> + the real Postgres-backed
/// <c>IDbContextFactory</c> — no TestServer needed.
/// </summary>
[Collection("Postgres")]
public sealed class McpEnabledGateMiddlewareTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateContext();
        await db.SpaceAiSettings.IgnoreQueryFilters().ExecuteDeleteAsync();
        McpEnabledGateMiddleware.ClearCacheForTest();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Passes_request_through_when_McpEnabled_is_true()
    {
        await SeedSettingsAsync(mcpEnabled: true);

        var nextCalled = false;
        var middleware = NewMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var ctx = new DefaultHttpContext();

        await middleware.InvokeAsync(ctx);

        nextCalled.Should().BeTrue(
            because: "with McpEnabled on, the gate lets the request reach the MCP transport");
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task Short_circuits_with_403_when_McpEnabled_is_false()
    {
        await SeedSettingsAsync(mcpEnabled: false);

        var nextCalled = false;
        var middleware = NewMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(ctx);

        nextCalled.Should().BeFalse(
            because: "with MCP disabled for the Space, the gate must not forward to the transport");
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        ctx.Response.ContentType.Should().StartWith("application/json");
    }

    [Fact]
    public async Task Short_circuits_with_403_when_no_settings_row_exists()
    {
        // No SpaceAiSettings row at all → McpEnabled defaults to false → gate closed.
        // (InitializeAsync already cleared the table; seed nothing here.)
        McpEnabledGateMiddleware.ClearCacheForTest();

        var nextCalled = false;
        var middleware = NewMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(ctx);

        nextCalled.Should().BeFalse(
            because: "absent settings means MCP was never enabled — fail closed, not open");
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private McpEnabledGateMiddleware NewMiddleware(RequestDelegate next)
    {
        McpEnabledGateMiddleware.ClearCacheForTest();
        return new McpEnabledGateMiddleware(
            next, postgres.ScopeFactory, NullLogger<McpEnabledGateMiddleware>.Instance);
    }

    private async Task SeedSettingsAsync(bool mcpEnabled)
    {
        await using var db = postgres.CreateContext();
        db.SpaceAiSettings.Add(new SpaceAiSettings
        {
            SpaceId    = WellKnown.DefaultSpaceId,
            Provider   = KrakenAiProviderValue.Anthropic,
            McpEnabled = mcpEnabled,
        });
        await db.SaveChangesAsync();
    }
}
