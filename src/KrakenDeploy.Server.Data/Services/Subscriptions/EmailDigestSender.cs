using System.Text.Json;
using KrakenDeploy.Server.Core.Domain.Notifications;
using KrakenDeploy.Server.Core.Domain.Subscriptions;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace KrakenDeploy.Server.Data.Services.Subscriptions;

/// <summary>
/// Small helper consumed by <c>EmailDigestFlushJob</c> to send the
/// batched digest email. Kept separate from
/// <see cref="EmailImmediateTransport"/> because the digest body comes
/// from the flusher (multi-event aggregate) not the transport (single
/// event); reusing the IEventTransport surface would force a per-event
/// shape on the digest path.
///
/// <para>
/// Both paths reach the same MailKit handshake; if we add a "send via
/// system X instead of SMTP" feature later, the two callers (immediate
/// transport + digest sender) need to migrate together.
/// </para>
/// </summary>
public sealed class EmailDigestSender(SmtpSettingsService smtpSettings)
{
    private static readonly JsonSerializerOptions ConfigJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<EventTransportResult> SendAsync(
        EventSubscription subscription, string body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

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
                "Digest subscription has no recipients configured.");
        }

        var snapshot = await smtpSettings.GetDecryptedSettingsAsync(ct).ConfigureAwait(false);
        if (snapshot is null)
        {
            return EventTransportResult.Failure(
                "SMTP is not configured — open Configuration → SMTP and " +
                "save settings before digest emails can fire.");
        }
        var (settings, password) = snapshot.Value;
        if (!settings.Enabled)
        {
            return EventTransportResult.Failure(
                "SMTP master switch is off — flip 'Enable outbound mail' " +
                "in Configuration → SMTP to resume digest delivery.");
        }

        try
        {
            using var message = new MimeMessage();
            message.From.Add(new MailboxAddress(
                settings.FromDisplayName ?? "KrakenDeploy", settings.FromAddress));
            foreach (var r in config.Recipients)
            {
                message.To.Add(MailboxAddress.Parse(r));
            }
            message.Subject = $"[KrakenDeploy digest] {subscription.Name}";
            message.Body = new TextPart("plain") { Text = body };

            using var client = new SmtpClient { Timeout = settings.TimeoutSeconds * 1000 };
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

            return EventTransportResult.Success(
                $"Delivered to {config.Recipients.Count} recipient(s) via {settings.Host}:{settings.Port}.");
        }
        catch (Exception ex)
        {
            return EventTransportResult.Failure(ex.Message);
        }
    }

    private sealed record EmailConfig(IReadOnlyList<string>? Recipients = null);
}
