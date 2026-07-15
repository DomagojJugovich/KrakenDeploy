using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Subscriptions;
using KrakenDeploy.Server.Data.Services.Subscriptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Unit tests for <see cref="WebhookTransport"/>. Uses a stub
/// <see cref="HttpMessageHandler"/> to capture the outgoing request so
/// we can assert payload shape + HMAC signature without spinning up a
/// real HTTP listener. The end-to-end "poller → match → deliver"
/// integration goes through the postgres fixture in a separate file.
/// </summary>
public sealed class WebhookTransportTests
{
    [Fact]
    public async Task DeliverAsync_posts_to_configured_url_with_signed_payload()
    {
        const string Secret = "shh";
        var stub = new CapturingHandler(HttpStatusCode.OK, "delivered");
        var transport = NewTransport(stub);

        var sub = new EventSubscription
        {
            Name                = "test",
            SpaceId             = WellKnown.DefaultSpaceId,
            Transport           = SubscriptionTransport.Webhook,
            TransportConfigJson = $$"""{"url":"https://203.0.113.10/hook","secret":"{{Secret}}"}""",
        };
        var evt = new AuditEntry
        {
            EventType   = "Deployment.Failed",
            OccurredUtc = DateTimeOffset.UtcNow,
            UserDisplay = "alice@laus.hr",
            SpaceId     = WellKnown.DefaultSpaceId,
            SubjectType = "Deployment",
            SubjectId   = Guid.NewGuid().ToString(),
            Details     = "build #42 failed",
        };

        var result = await transport.DeliverAsync(sub, evt, default);

        result.Succeeded.Should().BeTrue();
        result.Detail.Should().StartWith("HTTP 200");

        stub.Captured.Should().NotBeNull("the transport must have made an HTTP call");
        stub.Captured!.Method.Should().Be(HttpMethod.Post);
        stub.Captured.RequestUri!.ToString().Should().Be("https://203.0.113.10/hook");

        // Signature header present + matches expected HMAC.
        stub.Captured.Headers.TryGetValues("X-Kraken-Signature", out var sigHeaderEnum)
            .Should().BeTrue("a configured secret must produce a signature header");
        var sigHeader = string.Join("", sigHeaderEnum!);
        sigHeader.Should().StartWith("sha256=");
        var expectedSig = "sha256=" + ComputeExpectedHmac(Secret, stub.CapturedBody!);
        sigHeader.Should().Be(expectedSig,
            "the HMAC must be sha256(secret, raw-body) hex-lowercased — " +
            "matches the Stripe / GitHub convention so consumers use " +
            "off-the-shelf verifiers");

        // Subscription + event id mirrored in the request headers for
        // consumers that want trace-without-parsing-body.
        stub.Captured.Headers.GetValues("X-Kraken-Subscription-Id")
            .Should().ContainSingle().Which.Should().Be(sub.Id.ToString());
        stub.Captured.Headers.GetValues("X-Kraken-Event-Id")
            .Should().ContainSingle().Which.Should().Be(evt.Id.ToString());
    }

    [Fact]
    public async Task DeliverAsync_uses_Octopus_compatible_payload_shape()
    {
        var stub = new CapturingHandler(HttpStatusCode.OK, "ok");
        var transport = NewTransport(stub);

        var sub = new EventSubscription
        {
            Name                = "test",
            SpaceId             = WellKnown.DefaultSpaceId,
            Transport           = SubscriptionTransport.Webhook,
            TransportConfigJson = """{"url":"https://203.0.113.10/h"}""", // no secret = no sig
        };
        var evt = new AuditEntry
        {
            EventType   = "Deployment.Failed",
            OccurredUtc = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
            UserDisplay = "alice@laus.hr",
            SpaceId     = WellKnown.DefaultSpaceId,
        };

        await transport.DeliverAsync(sub, evt, default);

        var bodyText = Encoding.UTF8.GetString(stub.CapturedBody!);
        using var doc = JsonDocument.Parse(bodyText);
        var root = doc.RootElement;

        // Octopus envelope shape: top-level Timestamp + EventType +
        // Payload {ServerUri, Subscription, Event, Batch*}. PascalCase.
        root.GetProperty("EventType").GetString().Should().Be("SubscriptionPayload",
            "Octopus parity — consumers built against their docs can target ours");
        root.GetProperty("Timestamp").ValueKind.Should().Be(JsonValueKind.String);

        var payload = root.GetProperty("Payload");
        payload.TryGetProperty("ServerUri", out _).Should().BeTrue();
        payload.GetProperty("Subscription").GetProperty("Name").GetString().Should().Be("test");
        payload.GetProperty("Event").GetProperty("EventType").GetString().Should().Be("Deployment.Failed");
        payload.GetProperty("BatchId").ValueKind.Should().Be(JsonValueKind.String);
        payload.GetProperty("TotalEventsInBatch").GetInt32().Should().Be(1);
        payload.GetProperty("EventNumberInBatch").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task DeliverAsync_omits_signature_header_when_no_secret_configured()
    {
        var stub = new CapturingHandler(HttpStatusCode.OK, "ok");
        var transport = NewTransport(stub);

        var sub = new EventSubscription
        {
            Name                = "test",
            SpaceId             = WellKnown.DefaultSpaceId,
            Transport           = SubscriptionTransport.Webhook,
            TransportConfigJson = """{"url":"https://203.0.113.10/h"}""",
        };
        var evt = new AuditEntry
        {
            EventType = "Test", OccurredUtc = DateTimeOffset.UtcNow,
            UserDisplay = "t", SpaceId = WellKnown.DefaultSpaceId,
        };

        await transport.DeliverAsync(sub, evt, default);

        stub.Captured!.Headers.Contains("X-Kraken-Signature").Should().BeFalse(
            "no secret → no signature header; URL secrecy is the only auth");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Non_2xx_response_yields_failure_without_echoing_body(HttpStatusCode status)
    {
        // A6 (T1-11): the downstream response body must NOT be echoed into the
        // delivery result. It is persisted to subscription_deliveries.error_message
        // and reflected into the audit log + history UI — echoing arbitrary
        // downstream content there is a readable-SSRF sink.
        var stub = new CapturingHandler(status, "SECRET-INTERNAL-BODY");
        var transport = NewTransport(stub);

        var result = await transport.DeliverAsync(
            new EventSubscription
            {
                Name                = "t",
                SpaceId             = WellKnown.DefaultSpaceId,
                Transport           = SubscriptionTransport.Webhook,
                TransportConfigJson = """{"url":"https://203.0.113.10/h"}""",
            },
            new AuditEntry { EventType = "Test", OccurredUtc = DateTimeOffset.UtcNow, UserDisplay = "t", SpaceId = WellKnown.DefaultSpaceId },
            default);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().StartWith($"HTTP {(int)status}");
        result.Error.Should().NotContain("SECRET-INTERNAL-BODY",
            "downstream response bodies must never be echoed into delivery history");
    }

    [Fact]
    public async Task Redirect_status_reported_as_failure_without_following()
    {
        // With AllowAutoRedirect off on the real handler a 3xx surfaces here.
        // The transport must report it as a delivery failure and not echo a body.
        var stub = new CapturingHandler(HttpStatusCode.Found, "Location body ignored");
        var transport = NewTransport(stub);

        var result = await transport.DeliverAsync(
            new EventSubscription
            {
                Name                = "t",
                SpaceId             = WellKnown.DefaultSpaceId,
                Transport           = SubscriptionTransport.Webhook,
                TransportConfigJson = """{"url":"https://203.0.113.10/h"}""",
            },
            new AuditEntry { EventType = "Test", OccurredUtc = DateTimeOffset.UtcNow, UserDisplay = "t", SpaceId = WellKnown.DefaultSpaceId },
            default);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("redirect");
        result.Error.Should().NotContain("Location body ignored");
    }

    [Theory]
    [InlineData("http://127.0.0.1/hook")]         // loopback — denied by default
    [InlineData("http://169.254.169.254/latest")] // metadata — hard-blocked
    [InlineData("http://10.0.0.5/hook")]          // RFC1918 — denied by default
    public async Task DeliverAsync_refuses_blocked_hosts_before_sending(string url)
    {
        var stub = new CapturingHandler(HttpStatusCode.OK, "ok");
        var transport = NewTransport(stub); // default policy: deny loopback/private

        var result = await transport.DeliverAsync(
            new EventSubscription
            {
                Name                = "t",
                SpaceId             = WellKnown.DefaultSpaceId,
                Transport           = SubscriptionTransport.Webhook,
                TransportConfigJson = $$"""{"url":"{{url}}"}""",
            },
            new AuditEntry { EventType = "Test", OccurredUtc = DateTimeOffset.UtcNow, UserDisplay = "t", SpaceId = WellKnown.DefaultSpaceId },
            default);

        result.Succeeded.Should().BeFalse();
        stub.Captured.Should().BeNull("the guard must refuse before any HTTP call is made");
    }

    [Fact]
    public async Task DeliverAsync_allows_private_host_when_allowlisted()
    {
        var stub = new CapturingHandler(HttpStatusCode.OK, "ok");
        var ssrf = new Net.SsrfOptions
        {
            Webhook = new Net.SsrfPolicy { AllowedHosts = ["10.0.0.0/8"] },
        };
        var transport = NewTransport(stub, ssrf);

        var result = await transport.DeliverAsync(
            new EventSubscription
            {
                Name                = "t",
                SpaceId             = WellKnown.DefaultSpaceId,
                Transport           = SubscriptionTransport.Webhook,
                TransportConfigJson = """{"url":"http://10.0.0.5/hook"}""",
            },
            new AuditEntry { EventType = "Test", OccurredUtc = DateTimeOffset.UtcNow, UserDisplay = "t", SpaceId = WellKnown.DefaultSpaceId },
            default);

        result.Succeeded.Should().BeTrue("an allowlisted RFC1918 host must be reachable");
        stub.Captured.Should().NotBeNull();
    }

    [Fact]
    public async Task Network_failure_yields_failure_result_not_exception()
    {
        var stub = new ThrowingHandler(new HttpRequestException("DNS failed"));
        var transport = NewTransport(stub);

        var result = await transport.DeliverAsync(
            new EventSubscription
            {
                Name                = "t",
                SpaceId             = WellKnown.DefaultSpaceId,
                Transport           = SubscriptionTransport.Webhook,
                TransportConfigJson = """{"url":"https://203.0.113.10/h"}""",
            },
            new AuditEntry { EventType = "Test", OccurredUtc = DateTimeOffset.UtcNow, UserDisplay = "t", SpaceId = WellKnown.DefaultSpaceId },
            default);

        result.Succeeded.Should().BeFalse(
            "transport must never throw — the dispatcher relies on the " +
            "result shape, not exception handling");
        result.Error.Should().Contain("DNS failed");
    }

    [Fact]
    public void ComputeHmacHex_matches_reference_implementation()
    {
        // Lock in the HMAC recipe so a refactor doesn't silently produce
        // a different signature consumers can't verify. Reference vector
        // computed independently from RFC 4231 + a quick openssl check.
        var body = Encoding.UTF8.GetBytes("""{"hello":"world"}""");
        var sig  = WebhookTransport.ComputeHmacHex("secret", body);

        // Reference vector — independently verified with
        //   echo -n '{"hello":"world"}' | openssl dgst -sha256 -hmac "secret"
        sig.Should().Be("2677ad3e7c090b2fa2c0fb13020d66d5420879b8316eb356a2d60fb9073bc778",
            "HMAC-SHA256(secret, '{\"hello\":\"world\"}') — operator-facing " +
            "consumers will recompute against this exact recipe");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static WebhookTransport NewTransport(
        HttpMessageHandler handler, Net.SsrfOptions? ssrf = null)
    {
        var client = new HttpClient(handler);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Server:BaseUrl"] = "https://kraken.example.com",
            })
            .Build();
        return new WebhookTransport(
            client, config,
            Microsoft.Extensions.Options.Options.Create(ssrf ?? new Net.SsrfOptions()),
            NullLogger<WebhookTransport>.Instance, TimeProvider.System);
    }

    private static string ComputeExpectedHmac(string secret, byte[] body)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var mac = HMACSHA256.HashData(keyBytes, body);
        return Convert.ToHexStringLower(mac);
    }

    private sealed class CapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Captured { get; private set; }
        public byte[]?              CapturedBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Captured = request;
            CapturedBody = request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync(ct);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
            };
        }
    }

    private sealed class ThrowingHandler(Exception toThrow) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) => throw toThrow;
    }
}
