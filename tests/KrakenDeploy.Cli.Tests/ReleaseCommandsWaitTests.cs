using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using KrakenDeploy.Cli.Commands;
using KrakenDeploy.Server.Core.Domain.Deployments;

namespace KrakenDeploy.Cli.Tests;

/// <summary>
/// Tests for the <c>kraken release deploy --wait</c> polling loop.
/// The terminal-status set is string-based (the CLI talks REST and must not
/// depend on Server.Core), so a drift test pins it against the server's
/// authoritative <see cref="DeploymentStatusExtensions.IsTerminal"/> — the
/// original bug was exactly this set silently missing SucceededWithWarnings,
/// which made <c>--wait</c> poll until the timeout on a finished deployment.
/// </summary>
public sealed class ReleaseCommandsWaitTests
{
    // ── Drift guards against the server's terminal-status authority ──────────

    [Fact]
    public void TerminalStatuses_matches_DeploymentStatusExtensions_IsTerminal()
    {
        var authoritative = Enum.GetValues<DeploymentStatus>()
            .Where(s => s.IsTerminal())
            .Select(s => s.ToString());

        ReleaseCommands.TerminalStatuses.Should().BeEquivalentTo(
            authoritative,
            because: "the CLI's string set must track the server's IsTerminal " +
                     "authority — a status missing here makes --wait poll forever");
    }

    [Fact]
    public void Non_terminal_statuses_are_not_in_the_terminal_set()
    {
        var nonTerminal = Enum.GetValues<DeploymentStatus>()
            .Where(s => !s.IsTerminal())
            .Select(s => s.ToString());

        // PendingOfflineResult in particular is deliberately non-terminal:
        // the deployment is parked awaiting an out-of-band result bundle.
        nonTerminal.Should().Contain(nameof(DeploymentStatus.PendingOfflineResult));
        ReleaseCommands.TerminalStatuses.Should().NotContain(nonTerminal);
    }

    [Fact]
    public void SuccessStatuses_is_a_subset_of_TerminalStatuses()
    {
        ReleaseCommands.SuccessStatuses.Should().BeSubsetOf(ReleaseCommands.TerminalStatuses);
        ReleaseCommands.SuccessStatuses.Should().BeEquivalentTo(
            new[] { nameof(DeploymentStatus.Succeeded), nameof(DeploymentStatus.SucceededWithWarnings) },
            because: "SucceededWithWarnings means every Required step passed, " +
                     "so CI gates treat it as success (exit code 0)");
    }

    // ── Wait-loop behaviour ───────────────────────────────────────────────────

    [Theory]
    [InlineData("Succeeded", 0)]
    [InlineData("SucceededWithWarnings", 0)]
    [InlineData("Failed", 1)]
    [InlineData("Cancelled", 1)]
    public async Task WaitForDeploymentAsync_returns_promptly_with_exit_code_for_terminal_status(
        string status, int expectedExitCode)
    {
        var deploymentId = Guid.NewGuid();
        using var client = BuildClient(deploymentId, status);

        // Generous timeout: a terminal status must return on the FIRST poll,
        // long before the deadline. The pre-fix bug made SucceededWithWarnings
        // fall through to this timeout.
        var exitCode = await ReleaseCommands.WaitForDeploymentAsync(
            client, deploymentId, timeoutSeconds: 30);

        exitCode.Should().Be(expectedExitCode);
    }

    [Theory]
    [InlineData("Queued")]
    [InlineData("PendingOfflineResult")]
    public async Task WaitForDeploymentAsync_keeps_polling_non_terminal_status_until_timeout(
        string status)
    {
        var deploymentId = Guid.NewGuid();
        using var client = BuildClient(deploymentId, status);

        // 1s deadline → exactly one poll iteration, then the timeout exit
        // code. Proves the status is treated as non-terminal without waiting
        // for a real deployment-length timeout.
        var exitCode = await ReleaseCommands.WaitForDeploymentAsync(
            client, deploymentId, timeoutSeconds: 1);

        exitCode.Should().Be(2, because: "a non-terminal status must keep polling until the deadline");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="KrakenApiClient"/> whose transport is faked:
    /// <c>GET api/deployments/{id}/logs</c> returns an empty list and
    /// <c>GET api/deployments/{id}</c> returns a deployment with the given
    /// status. Same reflection swap as <see cref="KrakenApiClientTests"/>.
    /// </summary>
    private static KrakenApiClient BuildClient(Guid deploymentId, string status)
    {
        var handler = new FakeHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            object payload = path.EndsWith("/logs", StringComparison.OrdinalIgnoreCase)
                ? Array.Empty<object>()
                : new
                {
                    Id            = deploymentId,
                    Status        = status,
                    ReleaseId     = Guid.NewGuid(),
                    EnvironmentId = Guid.NewGuid(),
                    CreatedUtc    = DateTimeOffset.UtcNow,
                    StartedUtc    = (DateTimeOffset?)null,
                    CompletedUtc  = (DateTimeOffset?)null,
                };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(payload),
            };
        });

        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") };
        http.DefaultRequestHeaders.Add("X-Api-Key", "test-key");

        var client = new KrakenApiClient("http://localhost:5000", "test-key");
        var field = typeof(KrakenApiClient)
            .GetField("_http", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        ((IDisposable)field.GetValue(client)!).Dispose();
        field.SetValue(client, http);

        return client;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
