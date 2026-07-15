using System.IO.Pipelines;
using System.Security.Claims;
using FluentAssertions;
using KrakenDeploy.Mcp;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// M11.B Commit 5 — end-to-end MCP protocol test. Pairs a real
/// <see cref="McpServer"/> (built from the same AddKrakenMcp +
/// AddKrakenDeployData DI the production server uses) with a real
/// <see cref="McpClient"/> over in-memory duplex pipes. This exercises the
/// seam the per-method unit tests can't: the SDK handshake, tool-schema
/// generation from the [McpServerTool] attributes, per-request DI scope
/// resolution of the builders, and actual tool dispatch + resource reads
/// over the wire protocol.
/// <para>
/// In-memory streams rather than Kestrel + HTTP: the HTTP transport + the
/// API-key auth + the per-Space enable gate are exercised separately
/// (McpEnabledGateMiddlewareTests); this test targets the protocol +
/// dispatch layer.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class McpIntegrationTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateContext();
        // FK-safe order: server_tasks RESTRICT-reference releases + environments,
        // and releases RESTRICT-reference projects. Delete children first so each
        // test starts from a clean slate regardless of what the previous one seeded.
        await db.ServerTasks.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.Releases.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.Projects.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.Environments.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _serverLogs = new();

    private sealed class CaptureLoggerProvider(System.Collections.Concurrent.ConcurrentQueue<string> sink)
        : Microsoft.Extensions.Logging.ILoggerProvider
    {
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Capture(categoryName, sink);
        public void Dispose() { }

        private sealed class Capture(string category, System.Collections.Concurrent.ConcurrentQueue<string> sink)
            : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(
                Microsoft.Extensions.Logging.LogLevel logLevel,
                Microsoft.Extensions.Logging.EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning)
                {
                    sink.Enqueue($"[{logLevel}] {category}: {formatter(state, exception)} {exception}");
                }
            }
        }
    }

    [Fact]
    public async Task Client_lists_tools_and_resource_templates_then_round_trips_a_tool_and_resource()
    {
        await SeedProjectWithReleaseAsync();

        await WithMcpClientAsync(async (client, ct) =>
        {
            // ── Handshake + capability discovery ───────────────────────────
            var tools = await client.ListToolsAsync(cancellationToken: ct);
            var toolNames = tools.Select(t => t.Name).ToList();
            foreach (var expected in ExpectedToolNames)
            {
                toolNames.Should().Contain(expected);
            }

            var templates = await client.ListResourceTemplatesAsync(cancellationToken: ct);
            templates.Select(t => t.UriTemplate).Should()
                .Contain(u => u.Contains("kraken://projects/{projectSlug}/process"));

            // ── Round-trip a tool ──────────────────────────────────────────
            var historyResult = await client.CallToolAsync(
                "get_release_history",
                new Dictionary<string, object?> { ["projectSlug"] = "argosy", ["count"] = 0 },
                cancellationToken: ct);
            historyResult.IsError.Should().NotBe(true);
            ContentText(historyResult).Should().Contain("1.0");

            // ── Round-trip a resource ──────────────────────────────────────
            // Read the release's frozen process (the snapshot we seeded carries
            // the "Deploy" step; the LIVE project process resource would be empty
            // here since no DeploymentProcess row was seeded).
            var processResource = await client.ReadResourceAsync(
                "kraken://releases/argosy/1.0/process", cancellationToken: ct);
            var resourceText = processResource.Contents
                .OfType<TextResourceContents>().First().Text;
            resourceText.Should().Contain("Argosy").And.Contain("Deploy").And.Contain("1.0");
        });
    }

    [Fact]
    public async Task Failed_deployment_tool_serializes_status_as_enum_name()
    {
        // Regression for the REST<->MCP enum-wire divergence: after the REST fix
        // (commit 0cf2445) the API sends "Failed"; MCP must too. This drives the
        // real SDK marshalling (WithToolsFromAssembly + McpJsonOptions.ForTools),
        // proving the passed options reach tool-result serialization.
        await SeedFailedDeploymentAsync();

        await WithMcpClientAsync(async (client, ct) =>
        {
            // Empty args: the filters now carry defaults, so the SDK marks them
            // optional. Before that fix this call threw "missing a value for the
            // required parameter 'environmentName'".
            var result = await client.CallToolAsync(
                "list_failed_deployments",
                new Dictionary<string, object?>(),
                cancellationToken: ct);

            var text = ContentText(result);
            result.IsError.Should().NotBe(true,
                because: "tool errored: {0} | server logs: {1}", text, string.Join(" || ", _serverLogs));
            text.Should().Contain("\"Failed\"",
                because: "MCP tool output must carry the enum NAME, matching the REST wire");
            text.Should().NotContain("\"status\":3",
                because: "the numeric enum form is exactly the divergence being fixed");
        });
    }

    [Fact]
    public async Task Optional_filter_tool_accepts_empty_arguments()
    {
        // query_targets' role/environmentName filters are optional; calling with
        // no arguments must succeed. Before defaults were added the SDK required
        // every filter key and this threw. No seed needed — an empty target set
        // is a valid, non-error result.
        await WithMcpClientAsync(async (client, ct) =>
        {
            var result = await client.CallToolAsync(
                "query_targets",
                new Dictionary<string, object?>(),
                cancellationToken: ct);

            result.IsError.Should().NotBe(true,
                because: "optional filters must be omittable | server logs: {0}",
                string.Join(" || ", _serverLogs));
        });
    }

    private static readonly string[] ExpectedToolNames =
    [
        "list_failed_deployments", "get_deployment_log", "get_deployment_diff",
        "get_step_config", "retry_deployment", "get_target_health",
        "query_targets", "get_release_history",
    ];

    private static string ContentText(CallToolResult result)
        => string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    /// <summary>
    /// Builds the production DI graph (AddKrakenMcp + AddKrakenDeployData), wires a
    /// real <see cref="McpServer"/> to a real <see cref="McpClient"/> over in-memory
    /// duplex pipes, invokes <paramref name="body"/>, then tears everything down.
    /// </summary>
    private async Task WithMcpClientAsync(Func<McpClient, CancellationToken, Task> body)
    {
        var services = new ServiceCollection();
        services.AddKrakenDeployData(postgres.ConnectionString);
        services.AddKrakenMcp();
        services.AddLogging(b => b.AddProvider(new CaptureLoggerProvider(_serverLogs)));
        // T1-9 (A5): MCP read tools + resources now authorize via McpToolAuth,
        // which needs an authenticated principal (IHttpContextAccessor) and an
        // IPermissionEvaluator. This test's subject is the protocol + dispatch
        // seam, not authorization (that lives in McpToolTests /
        // McpEnabledGateMiddlewareTests), so supply an authed principal and an
        // allow-all evaluator. The last registration wins over the ones
        // AddKrakenDeployData added. Use an instance-backed accessor (not the
        // AsyncLocal HttpContextAccessor) so the principal is visible on the
        // server's dispatch flow regardless of async context.
        services.AddSingleton<IHttpContextAccessor>(new FixedHttpContextAccessor(
            new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Name, "mcp-integration-test"),
                ], authenticationType: "test")),
            }));
        services.AddScoped<IPermissionEvaluator>(_ => new AllowAllPermissionEvaluator());
        await using var sp = services.BuildServiceProvider();

        // Two duplex pipes: client↔server.
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        await using var serverTransport = new StreamServerTransport(
            inputStream:  clientToServer.Reader.AsStream(),
            outputStream: serverToClient.Writer.AsStream(),
            serverName:   "kraken-deploy");

        var options = sp.GetRequiredService<IOptions<McpServerOptions>>().Value;
        await using var server = McpServer.Create(
            serverTransport, options, sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>(), sp);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverRun = server.RunAsync(cts.Token);

        // Param names are server-perspective: serverInput is the channel the
        // client WRITES into (the server reads it); serverOutput is the
        // channel the client READS (the server writes it).
        var clientTransport = new StreamClientTransport(
            serverInput:  clientToServer.Writer.AsStream(),
            serverOutput: serverToClient.Reader.AsStream());
        await using var client = await McpClient.CreateAsync(
            clientTransport, clientOptions: null, loggerFactory: null, cts.Token);

        await body(client, cts.Token);

        await cts.CancelAsync();
        try { await serverRun; } catch (OperationCanceledException) { }
    }

    /// <summary>Instance-backed <see cref="IHttpContextAccessor"/> — unlike the
    /// framework's AsyncLocal-backed accessor, it returns the same context on any
    /// async flow, so the MCP server's dispatch tasks see the seeded principal.</summary>
    private sealed class FixedHttpContextAccessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => context; set { } }
    }

    private async Task SeedProjectWithReleaseAsync()
    {
        await using var db = postgres.CreateContext();
        var project = new Project
        {
            SpaceId = WellKnown.DefaultSpaceId,
            ProjectGroupId = await TestData.EnsureProjectGroupAsync(db, WellKnown.DefaultSpaceId),
            Name = "Argosy",
            Slug = "argosy",
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        db.Releases.Add(new Release
        {
            SpaceId = WellKnown.DefaultSpaceId,
            ProjectId = project.Id,
            Version = "1.0",
            VariableSnapshotUpdatedUtc = DateTimeOffset.UtcNow,
            ProcessSnapshot =
            {
                new Core.Domain.Releases.StepSnapshot
                {
                    Id = Guid.NewGuid(), Name = "Deploy", StepType = "Octopus.Script",
                    SortOrder = 0, Config = new Dictionary<string, string>(),
                },
            },
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Seeds one Failed deployment (project + environment + release +
    /// the Deployment row) — enough for list_failed_deployments to return a
    /// summary whose Status is Failed.</summary>
    private async Task SeedFailedDeploymentAsync()
    {
        await using var db = postgres.CreateContext();
        var project = new Project
        {
            SpaceId = WellKnown.DefaultSpaceId,
            ProjectGroupId = await TestData.EnsureProjectGroupAsync(db, WellKnown.DefaultSpaceId),
            Name = "Argosy",
            Slug = "argosy",
        };
        var environment = new DeploymentEnvironment
        {
            SpaceId = WellKnown.DefaultSpaceId,
            Name = "Production",
            Slug = "production",
        };
        db.Projects.Add(project);
        db.Environments.Add(environment);
        await db.SaveChangesAsync();

        var release = new Release
        {
            SpaceId = WellKnown.DefaultSpaceId,
            ProjectId = project.Id,
            Version = "1.0",
            VariableSnapshotUpdatedUtc = DateTimeOffset.UtcNow,
        };
        db.Releases.Add(release);
        await db.SaveChangesAsync();

        db.Deployments.Add(new Deployment
        {
            SpaceId = WellKnown.DefaultSpaceId,
            ProjectId = project.Id,
            ReleaseId = release.Id,
            EnvironmentId = environment.Id,
            Status = DeploymentStatus.Failed,
            Cause = ServerTaskCause.Manual,
            CreatedByDisplay = "mcp-integration-test",
        });
        await db.SaveChangesAsync();
    }
}
