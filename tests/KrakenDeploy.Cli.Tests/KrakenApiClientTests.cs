using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using KrakenDeploy.Cli;

namespace KrakenDeploy.Cli.Tests;

/// <summary>
/// Unit tests for <see cref="KrakenApiClient"/>.
/// Uses a fake <see cref="HttpMessageHandler"/> to avoid any network calls.
/// </summary>
public sealed class KrakenApiClientTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static KrakenApiClient CreateClient(
        HttpStatusCode status, object body, out string? capturedApiKey)
    {
        string? key = null;
        var handler = new FakeHandler(req =>
        {
            key = req.Headers.TryGetValues("X-Api-Key", out var vals)
                ? string.Concat(vals)
                : null;
            return new HttpResponseMessage(status)
            {
                Content = JsonContent.Create(body),
            };
        });
        capturedApiKey = null; // will be set on first call via closure

        var client = new KrakenApiClient("http://localhost:5000", "test-key");
        // Swap out the internal HttpClient — done via reflection for simplicity.
        // In production code we'd use IHttpClientFactory; for tests this is fine.
        var httpField = typeof(KrakenApiClient)
            .GetField("_http", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var existingHttp = (HttpClient)httpField.GetValue(client)!;
        existingHttp.Dispose();

        var fakeHttp = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") };
        fakeHttp.DefaultRequestHeaders.Add("X-Api-Key", "test-key");
        httpField.SetValue(client, fakeHttp);

        // Return the closure-captured key after the first call.
        capturedApiKey = key; // still null until first call — that's expected
        return client;
    }

    // ── GetProjectsAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetProjectsAsync_deserialises_list_correctly()
    {
        var payload = new[]
        {
            new { Id = Guid.NewGuid(), Name = "My App", Slug = "my-app", Description = (string?)null },
        };

        var handler = new FakeHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload) });

        using var client = BuildClient(handler);
        var projects = await client.GetProjectsAsync();

        projects.Should().ContainSingle();
        projects[0].Name.Should().Be("My App");
        projects[0].Slug.Should().Be("my-app");
    }

    [Fact]
    public async Task GetProjectBySlugAsync_returns_null_on_404()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = BuildClient(handler);

        var result = await client.GetProjectBySlugAsync("no-such-slug");

        result.Should().BeNull();
    }

    // ── GetTargetsAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetTargetsAsync_deserialises_status_field()
    {
        var payload = new[]
        {
            new
            {
                Id          = Guid.NewGuid(),
                Name        = "prod-01",
                Status      = "Online",
                MachineName = "PROD01",
                OperatingSystem = "Windows",
                AgentVersion    = "0.1.0",
                Roles           = new[] { "web" },
                LastSeenUtc     = DateTimeOffset.UtcNow,
            },
        };

        var handler = new FakeHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload) });

        using var client = BuildClient(handler);
        var targets = await client.GetTargetsAsync();

        targets.Should().ContainSingle();
        targets[0].Status.Should().Be("Online");
        targets[0].Roles.Should().ContainSingle("web");
    }

    // ── GetDeploymentLogsAsync ────────────────────────────────────────────────

    [Fact]
    public async Task GetDeploymentLogsAsync_passes_from_in_query_string()
    {
        string? capturedUri = null;
        var handler = new FakeHandler(req =>
        {
            capturedUri = req.RequestUri?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(Array.Empty<object>()),
            };
        });

        using var client = BuildClient(handler);
        await client.GetDeploymentLogsAsync(Guid.NewGuid(), from: 42);

        capturedUri.Should().Contain("from=42",
            because: "the from parameter must be forwarded as a query string");
    }

    // ── X-Api-Key header ──────────────────────────────────────────────────────

    [Fact]
    public async Task All_requests_carry_X_Api_Key_header()
    {
        string? capturedKey = null;
        var handler = new FakeHandler(req =>
        {
            capturedKey = req.Headers.TryGetValues("X-Api-Key", out var vals)
                ? string.Concat(vals)
                : null;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(Array.Empty<object>()),
            };
        });

        using var client = BuildClient(handler, apiKey: "my-secret-key");
        await client.GetProjectsAsync();

        capturedKey.Should().Be("my-secret-key",
            because: "every request must include the X-Api-Key header");
    }

    // ── CreateDeploymentAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task CreateDeploymentAsync_posts_correct_body()
    {
        string? capturedBody = null;
        var releaseId = Guid.NewGuid();
        var envId     = Guid.NewGuid();
        var targetId  = Guid.NewGuid();

        var response = new
        {
            Id            = Guid.NewGuid(),
            Status        = "Queued",
            ReleaseId     = releaseId,
            EnvironmentId = envId,
            CreatedUtc    = DateTimeOffset.UtcNow,
            StartedUtc    = (DateTimeOffset?)null,
            CompletedUtc  = (DateTimeOffset?)null,
        };

        var handler = new FakeHandler(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonContent.Create(response),
            };
        });

        using var client = BuildClient(handler);
        var deployment = await client.CreateDeploymentAsync(
            releaseId, envId, targetId,
            new Dictionary<string, string> { ["Greeting"] = "hello=world" });

        deployment.Status.Should().Be("Queued");

        capturedBody.Should().NotBeNullOrEmpty();
        // PostAsJsonAsync serialises the anonymous type with PascalCase property names
        // (no camelCase naming policy is set on JsonOpts).
        var doc = JsonDocument.Parse(capturedBody!).RootElement;
        doc.GetProperty("ReleaseId").GetGuid().Should().Be(releaseId);
        doc.GetProperty("EnvironmentId").GetGuid().Should().Be(envId);
        // TargetId is mandatory — the 2026-07 fix stopped the CLI from
        // serialising a null that bound as Guid.Empty server-side.
        doc.GetProperty("TargetId").GetGuid().Should().Be(targetId);
        doc.GetProperty("PromptedValues").GetProperty("Greeting").GetString()
            .Should().Be("hello=world");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static KrakenApiClient BuildClient(
        HttpMessageHandler handler, string apiKey = "test-key")
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") };
        http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

        var client = new KrakenApiClient("http://localhost:5000", apiKey);

        // Replace the internal HttpClient via reflection.
        var field = typeof(KrakenApiClient)
            .GetField("_http", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        ((IDisposable)field.GetValue(client)!).Dispose();
        field.SetValue(client, http);

        return client;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> sync)
            : this(req => Task.FromResult(sync(req))) { }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request);
    }
}
