using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Subscriptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Services.Subscriptions;

/// <summary>
/// HTTP POST delivery. Payload shape mirrors Octopus's documented
/// SubscriptionPayload envelope so a webhook consumer that targets
/// Octopus can be repointed at KrakenDeploy with minimal mapping.
///
/// <para>
/// Body wrapped in:
/// <code>
/// {
///   "Timestamp": "iso-8601",
///   "EventType": "SubscriptionPayload",
///   "Payload": {
///     "ServerUri": "https://...",
///     "Subscription": { "Id": "...", "Name": "..." },
///     "Event":        { "Id": "...", "Type": "...", ... },
///     "BatchProcessingDate": "iso-8601",
///     "BatchId": "guid",
///     "TotalEventsInBatch": 1,
///     "EventNumberInBatch": 1
///   }
/// }
/// </code>
/// </para>
///
/// <para>
/// HMAC-SHA256 signature over the raw request body, header
/// <c>X-Kraken-Signature: sha256=&lt;hex&gt;</c>. Secret comes from the
/// subscription's <c>TransportConfigJson.secret</c> field. Consumer
/// recomputes the signature to authenticate — same recipe Stripe /
/// GitHub use. No secret → no signature header; the URL is the only
/// access control.
/// </para>
/// </summary>
public sealed class WebhookTransport(
    HttpClient httpClient,
    IConfiguration configuration,
    ILogger<WebhookTransport> logger,
    TimeProvider time) : IEventTransport
{
    public SubscriptionTransport Transport => SubscriptionTransport.Webhook;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented        = false,
        PropertyNamingPolicy = null, // PascalCase, matching Octopus payload shape
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Case-insensitive config deserialiser — operator JSON is
    /// typically camelCase / lowercase but the record properties use C#
    /// PascalCase.</summary>
    private static readonly JsonSerializerOptions ConfigJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<EventTransportResult> DeliverAsync(
        EventSubscription subscription,
        AuditEntry auditEvent,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(auditEvent);

        WebhookConfig config;
        try
        {
            // Case-insensitive — config JSON is operator-authored,
            // typically camelCase or lowercase; the record properties are
            // PascalCase by C# convention.
            config = JsonSerializer.Deserialize<WebhookConfig>(
                subscription.TransportConfigJson, ConfigJsonOpts)
                ?? throw new InvalidOperationException("config deserialised to null");
        }
        catch (Exception ex)
        {
            return EventTransportResult.Failure(
                $"Malformed webhook config (rejected at save should have caught this): {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(config.Url))
        {
            return EventTransportResult.Failure("Webhook url is empty.");
        }

        // Build payload.
        var serverUri = configuration["Server:BaseUrl"];
        var payload = new SubscriptionPayloadEnvelope(
            Timestamp: time.GetUtcNow(),
            EventType: "SubscriptionPayload",
            Payload:   new SubscriptionPayload(
                ServerUri:           serverUri,
                Subscription:        SubscriptionShape.From(subscription),
                Event:               EventShape.From(auditEvent),
                BatchProcessingDate: time.GetUtcNow(),
                BatchId:             Guid.CreateVersion7(),
                TotalEventsInBatch:  1,
                EventNumberInBatch:  1));

        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);

        using var content = new ByteArrayContent(bodyBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, config.Url)
        {
            Content = content,
        };
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(
            "KrakenDeploy-Webhook", typeof(WebhookTransport).Assembly.GetName().Version?.ToString() ?? "1.0"));
        request.Headers.Add("X-Kraken-Subscription-Id", subscription.Id.ToString());
        request.Headers.Add("X-Kraken-Event-Id",        auditEvent.Id.ToString());

        if (!string.IsNullOrEmpty(config.Secret))
        {
            var sig = ComputeHmacHex(config.Secret, bodyBytes);
            request.Headers.Add("X-Kraken-Signature", "sha256=" + sig);
        }

        try
        {
            using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            // Per Octopus convention: any 2xx is success; everything else fails
            // (we let Hangfire retry on 5xx + transient; 4xx repeats are still
            // attempted by the default retry policy — operator configuration
            // bug is the typical 4xx cause).
            if (response.IsSuccessStatusCode)
            {
                logger.LogDebug(
                    "Webhook OK: sub={SubId} event={EventId} status={Status}",
                    subscription.Id, auditEvent.Id, status);
                return EventTransportResult.Success(
                    $"HTTP {status}; {response.ReasonPhrase ?? "OK"}");
            }
            // Read up to 512 chars of the response body for the error blurb —
            // helps the operator see "your endpoint says 'invalid signature'"
            // without exposing arbitrary downstream content.
            string? snippet = null;
            try
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                snippet = body.Length > 512 ? body[..512] + "…" : body;
            }
            catch { /* couldn't read body — fine, status is still informative */ }
            return EventTransportResult.Failure(
                $"HTTP {status} {response.ReasonPhrase}: {snippet}");
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            return EventTransportResult.Failure("Cancelled.");
        }
        catch (Exception ex)
        {
            return EventTransportResult.Failure(ex.Message);
        }
    }

    internal static string ComputeHmacHex(string secret, ReadOnlySpan<byte> body)
    {
        // SHA-256 HMAC; lowercase hex. Matches the Stripe / GitHub
        // convention so any HMAC-verifying library on the consumer side
        // works out of the box.
        Span<byte> mac = stackalloc byte[32];
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        HMACSHA256.HashData(keyBytes, body, mac);
        return Convert.ToHexStringLower(mac);
    }

    // ── DTOs ─────────────────────────────────────────────────────────────

    private sealed record WebhookConfig(string Url, string? Secret = null);

    internal sealed record SubscriptionPayloadEnvelope(
        DateTimeOffset Timestamp,
        string EventType,
        SubscriptionPayload Payload);

    internal sealed record SubscriptionPayload(
        string? ServerUri,
        SubscriptionShape Subscription,
        EventShape Event,
        DateTimeOffset BatchProcessingDate,
        Guid BatchId,
        int TotalEventsInBatch,
        int EventNumberInBatch);

    internal sealed record SubscriptionShape(
        Guid Id,
        string Name,
        Guid? SpaceId,
        string Transport)
    {
        public static SubscriptionShape From(EventSubscription s) =>
            new(s.Id, s.Name, s.SpaceId, s.Transport.ToString());
    }

    internal sealed record EventShape(
        Guid Id,
        string EventType,
        DateTimeOffset OccurredUtc,
        Guid? SpaceId,
        Guid? UserId,
        string? UserDisplay,
        string? SubjectType,
        string? SubjectId,
        string? SubjectName,
        string? Details)
    {
        public static EventShape From(AuditEntry e) =>
            new(e.Id, e.EventType, e.OccurredUtc, e.SpaceId,
                e.UserId, e.UserDisplay,
                e.SubjectType, e.SubjectId, e.SubjectName, e.Details);
    }
}
