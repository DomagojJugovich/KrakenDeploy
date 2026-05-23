using System.Text;
using System.Text.Json;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Notifications;
using KrakenDeploy.Server.Core.Domain.Subscriptions;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace KrakenDeploy.Server.Data.Services.Subscriptions;

/// <summary>
/// SMTP per-event delivery. Honoured when
/// <see cref="EventSubscription.DigestEveryMinutes"/> is 0 (immediate
/// mode); the same Transport=Email subscription with
/// <c>DigestEveryMinutes &gt; 0</c> takes a different path through the
/// digest outbox (Phase 5).
///
/// <para>
/// Reads <see cref="SmtpSettings"/> from the M13.B.1 store via
/// <see cref="SmtpSettingsService"/> — same Host/Port/TLS/Auth shape the
/// Settings → SMTP page exposes. Bails to a failure result when
/// settings aren't configured or <see cref="SmtpSettings.Enabled"/> is
/// false; the dispatcher's row write captures the reason so an operator
/// who turned off the master switch can see why their subscription
/// stopped firing.
/// </para>
/// </summary>
public sealed class EmailImmediateTransport(
    SmtpSettingsService smtpSettings,
    ILogger<EmailImmediateTransport> logger) : IEventTransport
{
    public SubscriptionTransport Transport => SubscriptionTransport.Email;

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

        // Digest mode goes through a different path (Phase 5).
        if (subscription.DigestEveryMinutes > 0)
        {
            return EventTransportResult.Failure(
                "Subscription is configured for digest mode; the digest " +
                "flusher handles delivery, not the immediate transport. " +
                "This is a routing bug — the dispatcher should not have " +
                "called the immediate transport for a digest subscription.");
        }

        EmailConfig config;
        try
        {
            config = JsonSerializer.Deserialize<EmailConfig>(
                subscription.TransportConfigJson, ConfigJsonOpts)
                ?? throw new InvalidOperationException("config deserialised to null");
        }
        catch (Exception ex)
        {
            return EventTransportResult.Failure(
                $"Malformed email transport config: {ex.Message}");
        }

        if (config.Recipients is null || config.Recipients.Count == 0)
        {
            return EventTransportResult.Failure(
                "Email transport requires at least one recipient.");
        }

        var smtpSnapshot = await smtpSettings.GetDecryptedSettingsAsync(ct).ConfigureAwait(false);
        if (smtpSnapshot is null)
        {
            return EventTransportResult.Failure(
                "SMTP is not configured — open Configuration → SMTP and " +
                "save settings before email subscriptions can fire.");
        }
        var (settings, password) = smtpSnapshot.Value;
        if (!settings.Enabled)
        {
            return EventTransportResult.Failure(
                "SMTP master switch is off — flip 'Enable outbound mail' " +
                "in Configuration → SMTP to resume email subscription delivery.");
        }

        try
        {
            using var message = BuildMessage(settings, config.Recipients, subscription, auditEvent);
            await SendAsync(settings, password, message, ct).ConfigureAwait(false);

            logger.LogInformation(
                "Email delivery ok: sub={SubId} event={EventId} recipients={Count}",
                subscription.Id, auditEvent.Id, config.Recipients.Count);

            return EventTransportResult.Success(
                $"Delivered to {config.Recipients.Count} recipient(s) via {settings.Host}:{settings.Port}.");
        }
        catch (Exception ex)
        {
            // MailKit's exception messages are operator-recognisable
            // ("Authentication failed", "Could not resolve host",
            // "The SMTP server does not support STARTTLS"). Surface
            // verbatim — same convention as SendProbeAsync.
            return EventTransportResult.Failure(ex.Message);
        }
    }

    private static MimeMessage BuildMessage(
        SmtpSettings settings,
        IReadOnlyList<string> recipients,
        EventSubscription subscription,
        AuditEntry e)
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(
            settings.FromDisplayName ?? "KrakenDeploy", settings.FromAddress));
        foreach (var r in recipients)
        {
            msg.To.Add(MailboxAddress.Parse(r));
        }
        msg.Subject = $"[KrakenDeploy] {e.EventType}";

        // Plain-text body — keeps the message short, copy-pasteable, and
        // avoids HTML-escaping landmines. The audit-log entry id is the
        // primary forensic handle; operators paste it into /audit search.
        var sb = new StringBuilder();
        sb.Append("Event:        ").Append(e.EventType).Append('\n');
        sb.Append("Occurred:     ").Append(e.OccurredUtc.ToString("O")).Append('\n');
        sb.Append("Subscription: ").Append(subscription.Name).Append('\n');
        if (e.SpaceId is not null)
        {
            sb.Append("Space:        ").Append(e.SpaceId).Append('\n');
        }
        if (!string.IsNullOrEmpty(e.UserDisplay))
        {
            sb.Append("Actor:        ").Append(e.UserDisplay).Append('\n');
        }
        if (e.SubjectType is not null || e.SubjectName is not null)
        {
            sb.Append("Subject:      ")
              .Append(e.SubjectType is not null ? e.SubjectType + " " : "")
              .Append(e.SubjectName ?? e.SubjectId ?? "")
              .Append('\n');
        }
        if (!string.IsNullOrEmpty(e.Details))
        {
            sb.Append("Details:\n  ").Append(e.Details.Replace("\n", "\n  ")).Append('\n');
        }
        sb.Append("\nEvent id: ").Append(e.Id).Append('\n');

        msg.Body = new TextPart("plain") { Text = sb.ToString() };
        return msg;
    }

    /// <summary>
    /// Connects, optionally authenticates, sends, and disconnects.
    /// Mirrors <c>SmtpSettingsService.SendProbeAsync</c>'s handshake so
    /// "test passes via the Configuration → SMTP test button" implies
    /// "subscription delivery will work too".
    /// </summary>
    private static async Task SendAsync(
        SmtpSettings settings, string? password,
        MimeMessage message, CancellationToken ct)
    {
        using var client = new SmtpClient
        {
            Timeout = settings.TimeoutSeconds * 1000,
        };

        var secureOption = settings.TlsMode switch
        {
            SmtpTlsMode.None                  => SecureSocketOptions.None,
            SmtpTlsMode.StartTlsWhenAvailable => SecureSocketOptions.StartTlsWhenAvailable,
            SmtpTlsMode.StartTlsRequired      => SecureSocketOptions.StartTls,
            SmtpTlsMode.ImplicitTls           => SecureSocketOptions.SslOnConnect,
            _                                 => SecureSocketOptions.StartTlsWhenAvailable,
        };

        await client.ConnectAsync(settings.Host, settings.Port, secureOption, ct)
            .ConfigureAwait(false);

        if (!string.IsNullOrEmpty(settings.Username) && !string.IsNullOrEmpty(password))
        {
            await client.AuthenticateAsync(settings.Username, password, ct)
                .ConfigureAwait(false);
        }

        await client.SendAsync(message, ct).ConfigureAwait(false);
        await client.DisconnectAsync(quit: true, ct).ConfigureAwait(false);
    }

    /// <summary>Schema for the subscription's TransportConfigJson when
    /// Transport=Email.</summary>
    internal sealed record EmailConfig(IReadOnlyList<string>? Recipients = null);
}
