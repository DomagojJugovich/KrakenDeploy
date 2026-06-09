using System.IO.Pipelines;
using FluentAssertions;
using KrakenDeploy.Mcp;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
        await db.Releases.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.Projects.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Client_lists_tools_and_resource_templates_then_round_trips_a_tool_and_resource()
    {
        await SeedProjectWithReleaseAsync();

        var services = new ServiceCollection();
        services.AddKrakenDeployData(postgres.ConnectionString);
        services.AddKrakenMcp();
        services.AddLogging();
        // IEncryptionService isn't needed for the MCP read path, but the
        // data extension's VariableService registration wants it resolvable
        // if touched. None of the exercised tools touch it; skip.
        await using var sp = services.BuildServiceProvider();

        // Two duplex pipes: client↔server.
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        await using var serverTransport = new StreamServerTransport(
            inputStream:  clientToServer.Reader.AsStream(),
            outputStream: serverToClient.Writer.AsStream(),
            serverName:   "kraken-deploy");

        var options = sp.GetRequiredService<IOptions<McpServerOptions>>().Value;
        await using var server = McpServer.Create(serverTransport, options, loggerFactory: null, sp);

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

        // ── Handshake + capability discovery ───────────────────────────
        var tools = await client.ListToolsAsync(cancellationToken: cts.Token);
        var toolNames = tools.Select(t => t.Name).ToList();
        foreach (var expected in ExpectedToolNames)
        {
            toolNames.Should().Contain(expected);
        }

        var templates = await client.ListResourceTemplatesAsync(cancellationToken: cts.Token);
        templates.Select(t => t.UriTemplate).Should()
            .Contain(u => u.Contains("kraken://projects/{projectSlug}/process"));

        // ── Round-trip a tool ──────────────────────────────────────────
        var historyResult = await client.CallToolAsync(
            "get_release_history",
            new Dictionary<string, object?> { ["projectSlug"] = "argosy", ["count"] = 0 },
            cancellationToken: cts.Token);
        historyResult.IsError.Should().NotBe(true);
        ContentText(historyResult).Should().Contain("1.0");

        // ── Round-trip a resource ──────────────────────────────────────
        // Read the release's frozen process (the snapshot we seeded carries
        // the "Deploy" step; the LIVE project process resource would be empty
        // here since no DeploymentProcess row was seeded).
        var processResource = await client.ReadResourceAsync(
            "kraken://releases/argosy/1.0/process", cancellationToken: cts.Token);
        var resourceText = processResource.Contents
            .OfType<TextResourceContents>().First().Text;
        resourceText.Should().Contain("Argosy").And.Contain("Deploy").And.Contain("1.0");

        await cts.CancelAsync();
        try { await serverRun; } catch (OperationCanceledException) { }
    }

    private static readonly string[] ExpectedToolNames =
    [
        "list_failed_deployments", "get_deployment_log", "get_deployment_diff",
        "get_step_config", "retry_deployment", "get_target_health",
        "query_targets", "get_release_history",
    ];

    private static string ContentText(CallToolResult result)
        => string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private async Task SeedProjectWithReleaseAsync()
    {
        await using var db = postgres.CreateContext();
        var project = new Project { SpaceId = WellKnown.DefaultSpaceId, Name = "Argosy", Slug = "argosy" };
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
}
