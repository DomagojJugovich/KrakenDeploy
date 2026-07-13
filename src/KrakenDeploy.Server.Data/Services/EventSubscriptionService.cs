using System.Text.Json;
using KrakenDeploy.Server.Core.Domain.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// CRUD + listing for <see cref="EventSubscription"/>. Validates the
/// transport-config-JSON against the chosen transport on every save so a
/// broken row can never reach the delivery worker.
///
/// <para>
/// Listing honours the nullable <see cref="EventSubscription.SpaceId"/>
/// pattern: ambient Space + system-wide rows merge into one list (system-
/// wide rows appear in every Space's UI but only sys-admin can edit them).
/// Cross-Space listing for sys-admin is a separate method.
/// </para>
/// </summary>
public sealed class EventSubscriptionService(
    IDbContextFactory<KrakenDbContext> dbFactory)
{
    /// <summary>
    /// All subscriptions visible from the supplied Space: Space-scoped
    /// rows where <c>SpaceId == spaceId</c>, plus every system-wide
    /// (SpaceId=null) row. System-wide rows appear in every Space's list
    /// — the operator needs to know what's firing against their Space.
    /// </summary>
    public async Task<List<EventSubscription>> GetForSpaceAsync(
        Guid spaceId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.EventSubscriptions
            .Where(s => s.SpaceId == spaceId || s.SpaceId == null)
            .OrderBy(s => s.SpaceId == null ? 0 : 1) // system-wide first
            .ThenBy(s => s.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>Every active (non-disabled) subscription across every
    /// Space. Used by the outbox poller — Space filtering is done by
    /// the matcher, not the query, so a single read covers all events.</summary>
    public async Task<List<EventSubscription>> GetAllActiveAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.EventSubscriptions
            .AsNoTracking()
            .Where(s => !s.Disabled)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<EventSubscription?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.EventSubscriptions
            .FirstOrDefaultAsync(s => s.Id == id, ct)
            .ConfigureAwait(false);
    }

    public async Task<EventSubscription> CreateAsync(
        EventSubscription input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        Validate(input);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.EventSubscriptions.Add(input);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return input;
    }

    public async Task<EventSubscription?> UpdateAsync(
        Guid id, EventSubscription input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        Validate(input);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.EventSubscriptions
            .FirstOrDefaultAsync(s => s.Id == id, ct)
            .ConfigureAwait(false);
        if (existing is null) { return null; }

        // SpaceId is intentionally NOT updatable — a Space-scoped
        // subscription moving to system-wide changes its security tier
        // (sys-admin only) so we treat that as delete + recreate.
        existing.Name                = input.Name;
        existing.Description         = input.Description;
        existing.EventTypePatterns   = [.. input.EventTypePatterns];
        existing.ProjectIds          = [.. input.ProjectIds];
        existing.EnvironmentIds      = [.. input.EnvironmentIds];
        existing.Transport           = input.Transport;
        existing.TransportConfigJson = input.TransportConfigJson;
        existing.DigestEveryMinutes  = input.DigestEveryMinutes;
        existing.Disabled            = input.Disabled;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return existing;
    }

    /// <summary>
    /// M11.C closes through here. Ensures a Space has a built-in
    /// "diagnose Deployment.Failed via AI" subscription — creates it if
    /// missing, leaves it alone otherwise. Operator drives this from the
    /// AI Settings page when they enable Diagnosis. The seeded
    /// subscription is a normal row — operator can edit / disable /
    /// delete it like any other; re-calling this method after delete
    /// re-creates the row (intentional — pin the "AI diagnose is on
    /// and this is the wire" mapping).
    /// </summary>
    public async Task<EventSubscription> EnsureDiagnoseDeploymentFailedAsync(
        Guid spaceId, CancellationToken ct = default)
    {
        const string BuiltInName = "Built-in: AI diagnose Deployment.Failed";

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.EventSubscriptions
            .FirstOrDefaultAsync(s =>
                s.SpaceId == spaceId &&
                s.Name == BuiltInName, ct)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return existing;
        }

        var seeded = new EventSubscription
        {
            SpaceId             = spaceId,
            Name                = BuiltInName,
            Description         = "Auto-created by 'Diagnose failures' on the AI Settings " +
                                  "page. Edit or disable like any subscription — recreating " +
                                  "the same name from the AI page after delete is intentional " +
                                  "(re-seeds the row).",
            EventTypePatterns   = ["Deployment.Failed"],
            Transport           = SubscriptionTransport.AiInspect,
            TransportConfigJson = "{}",  // default prompt template
            Disabled            = false,
        };
        db.EventSubscriptions.Add(seeded);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return seeded;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.EventSubscriptions
            .FirstOrDefaultAsync(s => s.Id == id, ct)
            .ConfigureAwait(false);
        if (existing is null) { return false; }

        // Delivery-history and queued-digest rows now carry a real FK
        // ON DELETE CASCADE to this subscription, so the DB removes them in
        // the same transaction — no more orphaned rows to leak or sweep.
        db.EventSubscriptions.Remove(existing);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    // ── Validation ─────────────────────────────────────────────────────────

    private static void Validate(EventSubscription input)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            throw new ArgumentException(
                "Subscription name is required.", nameof(input));
        }
        if (input.DigestEveryMinutes < 0)
        {
            throw new ArgumentException(
                "DigestEveryMinutes must be 0 (immediate) or positive.", nameof(input));
        }
        if (input.DigestEveryMinutes > 0 && input.Transport != SubscriptionTransport.Email)
        {
            throw new ArgumentException(
                "DigestEveryMinutes is only meaningful for the Email transport.",
                nameof(input));
        }

        // Parse the transport-config JSON to confirm it's well-formed +
        // matches the shape the transport expects. We don't deserialise
        // here (the transport owns its DTO); just validate JSON-shape.
        try
        {
            using var doc = JsonDocument.Parse(input.TransportConfigJson);
            ValidateTransportConfig(input.Transport, doc.RootElement);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException(
                $"TransportConfigJson is not valid JSON: {ex.Message}",
                nameof(input));
        }
    }

    private static void ValidateTransportConfig(
        SubscriptionTransport transport, JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "TransportConfigJson must be a JSON object.");
        }

        // Per-transport required fields. Schema-on-read at delivery time
        // handles the rest; we just want to catch the egregious cases
        // (operator pastes the wrong shape) at save time.
        switch (transport)
        {
            case SubscriptionTransport.Webhook:
                if (!config.TryGetProperty("url", out var url) ||
                    url.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(url.GetString()))
                {
                    throw new ArgumentException(
                        "Webhook transport requires a non-empty 'url' field.");
                }
                break;
            case SubscriptionTransport.Email:
                if (!config.TryGetProperty("recipients", out var recipients) ||
                    recipients.ValueKind != JsonValueKind.Array ||
                    recipients.GetArrayLength() == 0)
                {
                    throw new ArgumentException(
                        "Email transport requires a non-empty 'recipients' array.");
                }
                break;
            case SubscriptionTransport.Runbook:
                // RunbookService.TriggerAsync requires all three. Surface
                // the missing-field error at save time instead of letting
                // the operator wait for the first event to discover the gap.
                foreach (var requiredField in new[] { "runbookId", "environmentId", "targetId" })
                {
                    if (!config.TryGetProperty(requiredField, out var fieldValue) ||
                        fieldValue.ValueKind != JsonValueKind.String ||
                        !Guid.TryParse(fieldValue.GetString(), out _))
                    {
                        throw new ArgumentException(
                            $"Runbook transport requires a '{requiredField}' GUID field.");
                    }
                }
                break;
            case SubscriptionTransport.AiInspect:
                // No required fields — the transport falls back to a
                // built-in prompt template when 'prompt' is absent.
                break;
            default:
                throw new ArgumentException(
                    $"Unknown transport: {transport}");
        }
    }
}
