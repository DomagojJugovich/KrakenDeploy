using FluentAssertions;
using KrakenDeploy.Agent.Config;
using KrakenDeploy.Agent.Identity;
using KrakenDeploy.Agent.Services;
using KrakenDeploy.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Agent.Tests;

public sealed class RegistrationServiceTests : IAsyncDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), $"kraken-reg-test-{Guid.NewGuid():N}");

    // ── Tests ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_loads_existing_identity_without_calling_server()
    {
        // Pre-write an identity file so the service uses the "already registered" path.
        var existing = new AgentIdentity
        {
            AgentId = Guid.NewGuid(),
            AgentToken = "existing.jwt",
            ServerUrl = "https://localhost:5443",
        };
        await CreateStore().SaveAsync(existing, CancellationToken.None);

        var context = new AgentContext();
        var svc = CreateService(context, serverUrl: "https://localhost:5443", token: null);

        await svc.StartAsync(CancellationToken.None);
        await context.IdentityReady.WaitAsync(TimeSpan.FromSeconds(5));

        context.Identity.Should().NotBeNull();
        context.Identity!.AgentId.Should().Be(existing.AgentId);

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_registers_with_fake_server_and_persists_identity()
    {
        var expectedId = Guid.NewGuid();
        await using var fakeServer = await StartFakeServerAsync(expectedId, "issued.jwt");

        var context = new AgentContext();
        var svc = CreateService(context, serverUrl: fakeServer.BaseUrl, token: "any-one-time-token");

        await svc.StartAsync(CancellationToken.None);
        await context.IdentityReady.WaitAsync(TimeSpan.FromSeconds(10));

        context.Identity.Should().NotBeNull();
        context.Identity!.AgentId.Should().Be(expectedId);
        context.Identity.AgentToken.Should().Be("issued.jwt");
        context.Identity.ServerUrl.Should().Be(fakeServer.BaseUrl);

        // The identity must have been persisted to disk.
        var persisted = await CreateStore().TryLoadAsync(CancellationToken.None);
        persisted.Should().NotBeNull();
        persisted!.AgentId.Should().Be(expectedId);

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_calls_StopApplication_when_no_token_and_no_identity()
    {
        var context = new AgentContext();
        var lifetime = new FakeApplicationLifetime();
        var svc = CreateService(context, serverUrl: "https://localhost:5443", token: null,
            lifetime: lifetime);

        await svc.StartAsync(CancellationToken.None);

        // Give the background task a moment to detect the missing token and stop.
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        lifetime.StopApplicationCalled.Should().BeTrue(
            because: "the service must stop when there is no identity file and no registration token");

        await svc.StopAsync(CancellationToken.None);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private AgentIdentityStore CreateStore()
    {
        var opts = Options.Create(new AgentConfig { DataPath = _dataDir });
        return new AgentIdentityStore(opts, NullLogger<AgentIdentityStore>.Instance);
    }

    private RegistrationHostedService CreateService(
        AgentContext context,
        string serverUrl,
        string? token,
        FakeApplicationLifetime? lifetime = null)
    {
        var serverOpts = Options.Create(new ServerOptions
        {
            Url = serverUrl,
            RegistrationToken = token,
            // The fake registration server is local http:// — the dev override the
            // A8/T1-12 https gate is designed for. https URLs pass regardless.
            AllowInsecureHttp = true,
        });
        var agentOpts = Options.Create(new AgentConfig { DataPath = _dataDir });
        return new RegistrationHostedService(
            context,
            new AgentIdentityStore(agentOpts, NullLogger<AgentIdentityStore>.Instance),
            serverOpts,
            lifetime ?? new FakeApplicationLifetime(),
            NullLogger<RegistrationHostedService>.Instance);
    }

    private static async Task<FakeRegistrationServer> StartFakeServerAsync(
        Guid agentId, string jwt)
    {
        var port = GetFreePort();
        var app = WebApplication.Create(["--urls", $"http://127.0.0.1:{port}"]);
        // Silence default ASP.NET Core logging during tests.
        app.Lifetime.ApplicationStarted.Register(() => { });
        app.MapPost("/api/agents/register",
            () => Results.Ok(new RegisterAgentResponse(agentId, jwt, "Reverse")));
        await app.StartAsync();
        return new FakeRegistrationServer(app, $"http://127.0.0.1:{port}");
    }

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(
            System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }
}

// ── Test doubles ────────────────────────────────────────────────────────────

internal sealed class FakeRegistrationServer(WebApplication app, string baseUrl) : IAsyncDisposable
{
    public string BaseUrl => baseUrl;
    public ValueTask DisposeAsync() => app.DisposeAsync();
}

internal sealed class FakeApplicationLifetime : IHostApplicationLifetime, IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    public CancellationToken ApplicationStarted  => CancellationToken.None;
    public CancellationToken ApplicationStopping => _cts.Token;
    public CancellationToken ApplicationStopped  => CancellationToken.None;
    public bool StopApplicationCalled { get; private set; }

    public void StopApplication()
    {
        StopApplicationCalled = true;
        _cts.Cancel();
    }

    public void Dispose() => _cts.Dispose();
}
