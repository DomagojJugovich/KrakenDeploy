// KrakenDeploy.Router — the per-node blue-green slot router
// (docs/blue-green-slot-deployment.md §6, D-bg-3/D-bg-7).
//
// Runs on each app node beside the three slot instances. Owns exactly one
// decision: which LOCAL slot serves this request — the release pinned by the
// __Host-kd_ver cookie / X-KD-Release header, or the current default release
// for unpinned/stale requests (issuing the pin). Uses YARP direct forwarding
// (IHttpForwarder): no proxy config exists, so a default flip can never trigger
// a config reload or force-close live WebSockets.
//
// The edge (Caddy) stays version-agnostic and DB-free; this process holds the
// only catalog read the routing needs (two tables, cached, degrade-stale).

using System.Diagnostics;
using System.Net;
using KrakenDeploy.Router;
using Microsoft.Extensions.Options;
using Npgsql;
using Yarp.ReverseProxy.Forwarder;

var builder = WebApplication.CreateBuilder(args);

// IsNullOrWhiteSpace, not a null-coalesce: the shipped appsettings.json carries
// the key with an empty value as documentation, which must still fail fast.
var catalogConnectionString = builder.Configuration.GetConnectionString("Catalog");
if (string.IsNullOrWhiteSpace(catalogConnectionString))
{
    throw new InvalidOperationException(
        "ConnectionStrings:Catalog is not configured. The router reads the release " +
        "registry (app_releases + platform_settings) from the control-plane catalog.");
}

builder.Services.Configure<RouterOptions>(
    builder.Configuration.GetSection(RouterOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(catalogConnectionString));
builder.Services.AddSingleton<ReleaseSnapshotCache>();
builder.Services.AddHttpForwarder();

var app = builder.Build();

var cache = app.Services.GetRequiredService<ReleaseSnapshotCache>();
var forwarder = app.Services.GetRequiredService<IHttpForwarder>();
var routerOptions = app.Services.GetRequiredService<IOptions<RouterOptions>>();
var logger = app.Logger;

// Outbound client per YARP direct-forwarding guidance: no cookies, no redirects,
// no decompression (pass-through), multiplexed HTTP/2.
using var slotClient = new HttpMessageInvoker(new SocketsHttpHandler
{
    UseProxy = false,
    AllowAutoRedirect = false,
    AutomaticDecompression = DecompressionMethods.None,
    UseCookies = false,
    EnableMultipleHttp2Connections = true,
    ConnectTimeout = TimeSpan.FromSeconds(15),
    ActivityHeadersPropagator = new ReverseProxyPropagator(DistributedContextPropagator.Current),
});

// Long-lived SignalR/Blazor traffic keeps itself alive with 15s keepalives, so a
// 2-minute inactivity window is generous without letting dead flows linger.
var forwarderConfig = new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromMinutes(2) };

// ── Router self-endpoints (never forwarded; literal routes beat the catch-all) ──

// Router health: can we produce a routable snapshot? Note the app-level probe is
// different: GET /healthz (no pin) forwards to the DEFAULT slot and therefore
// checks router + default slot + its DB in one go — that is what the edge
// health-checks (§6). This endpoint isolates the router itself.
app.MapGet("/kd-router/healthz", async (CancellationToken ct) =>
{
    try
    {
        var snapshot = await cache.GetAsync(ct).ConfigureAwait(false);
        return snapshot.DefaultReleaseId is null
            ? Results.Json(
                new { status = "unhealthy", reason = "no current default release" },
                statusCode: StatusCodes.Status503ServiceUnavailable)
            : Results.Ok(new
            {
                status = "ok",
                defaultRelease = snapshot.DefaultReleaseId,
                liveReleases = snapshot.LiveReleases.Count,
            });
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return Results.Json(
            new { status = "unhealthy", reason = ex.Message },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

// Push-invalidation hook for deploy orchestration (flip/retire): the next
// request re-reads the catalog instead of waiting out the TTL. The router sits
// behind a pass-everything edge, so this REQUIRES the Router:OpsToken shared
// secret (X-KD-Ops-Token header) — without a configured token the endpoint is
// disabled (404) and routers converge via the cache TTL alone.
app.MapPost("/kd-router/invalidate", (HttpContext context) =>
{
    var expected = routerOptions.Value.OpsToken;
    if (string.IsNullOrWhiteSpace(expected))
    {
        return Results.NotFound();
    }

    var supplied = context.Request.Headers["X-KD-Ops-Token"].ToString();
    var suppliedBytes = System.Text.Encoding.UTF8.GetBytes(supplied);
    var expectedBytes = System.Text.Encoding.UTF8.GetBytes(expected);
    if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            suppliedBytes, expectedBytes))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    cache.Invalidate();
    return Results.NoContent();
});

// The slot-telemetry probe is an INTERNAL surface (drain-watcher → slot port
// directly); through the pass-everything edge it would leak live circuit /
// in-flight counts and the release id to anonymous internet clients. Never
// forward it.
app.Map("/slot-metrics", () => Results.NotFound());

// ── Everything else: pick the slot, forward ─────────────────────────────────

app.Map("/{**path}", async (HttpContext context) =>
{
    RouterSnapshot snapshot;
    try
    {
        snapshot = await cache.GetAsync(context.RequestAborted).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        logger.LogError(ex, "No release snapshot available (cold start with catalog down?).");
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(
            new { error = "Router cannot read the release registry." }).ConfigureAwait(false);
        return;
    }

    var decision = SlotRouteDecider.Decide(snapshot, VersionPin.Extract(context.Request));
    if (decision is null)
    {
        logger.LogError(
            "No routable default release (default: {Default}); refusing request.",
            snapshot.DefaultReleaseId ?? "<unset>");
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(
            new { error = "No routable default release is registered." }).ConfigureAwait(false);
        return;
    }

    if (!routerOptions.Value.Slots.TryGetValue(decision.SlotNo, out var destination))
    {
        logger.LogError(
            "Release {ReleaseId} maps to slot {Slot}, which has no configured destination " +
            "(Router:Slots).", decision.ReleaseId, decision.SlotNo);
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(
            new { error = $"Slot {decision.SlotNo} has no configured destination." })
            .ConfigureAwait(false);
        return;
    }

    // Stamp the resolved release: observability for operators/smoke, and how a
    // freshly registering agent learns its pin (it echoes this back as a header).
    context.Response.Headers[VersionPin.HeaderName] = decision.ReleaseId;
    if (decision.IssuePin)
    {
        VersionPin.Issue(context, decision.ReleaseId);
    }

    var error = await forwarder.SendAsync(
        context, destination, slotClient, forwarderConfig, HostPreservingTransformer.Instance)
        .ConfigureAwait(false);

    if (error != ForwarderError.None && !context.Response.HasStarted)
    {
        logger.LogWarning(
            "Forwarding to slot {Slot} ({Destination}) failed: {Error}.",
            decision.SlotNo, destination, error);
        context.Response.StatusCode = StatusCodes.Status502BadGateway;
        await context.Response.WriteAsJsonAsync(
            new { error = $"Slot {decision.SlotNo} unreachable ({error})." })
            .ConfigureAwait(false);
    }
});

await app.RunAsync().ConfigureAwait(false);
