using KrakenDeploy.Server.Core.Domain.Notifications;
using KrakenDeploy.Server.Core.Domain.Variables;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Server-wide SMTP configuration store + send pipeline.
///
/// <para>
/// Single-row table, addressed by <see cref="SmtpSettings.SingletonId"/>.
/// The password is AES-256-GCM-encrypted on persist and decrypted only at
/// send time — the GET path returns the row with <c>PasswordEncrypted</c>
/// nulled out so the cipher never reaches the browser. UI sends a null
/// password on PUT to mean "keep the existing one"; a non-null value
/// re-encrypts.
/// </para>
/// </summary>
public sealed class SmtpSettingsService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    IEncryptionService encryption,
    ILogger<SmtpSettingsService> logger)
{
    private static readonly Guid SingletonId = SmtpSettings.SingletonId;

    /// <summary>
    /// Returns the persisted settings or <see langword="null"/> if no row
    /// exists yet. The returned record has <c>PasswordEncrypted</c> set to
    /// <see langword="null"/> regardless of DB state — callers that need
    /// the cleartext password go through <see cref="GetDecryptedSettingsAsync"/>
    /// instead.
    /// </summary>
    public async Task<SmtpSettings?> GetAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.SmtpSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == SingletonId, ct)
            .ConfigureAwait(false);
        if (row is null) { return null; }
        // Belt-and-braces: never leak the cipher beyond the data layer.
        row.PasswordEncrypted = null;
        return row;
    }

    /// <summary>
    /// Internal-use overload that returns the row WITH the cipher attached.
    /// Used by the send pipeline only — explicitly different method so it's
    /// hard to accidentally bind to the public GET surface.
    /// </summary>
    public async Task<(SmtpSettings Settings, string? Password)?> GetDecryptedSettingsAsync(
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.SmtpSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == SingletonId, ct)
            .ConfigureAwait(false);
        if (row is null) { return null; }

        string? password = null;
        if (!string.IsNullOrEmpty(row.PasswordEncrypted))
        {
            password = encryption.Decrypt(row.PasswordEncrypted);
        }
        row.PasswordEncrypted = null;
        return (row, password);
    }

    /// <summary>
    /// Persists updated settings. <paramref name="newPassword"/> semantics:
    /// <list type="bullet">
    ///   <item><c>null</c> = leave existing password as-is.</item>
    ///   <item>empty string = clear the password (anonymous auth).</item>
    ///   <item>non-empty = encrypt + replace.</item>
    /// </list>
    /// </summary>
    public async Task<SmtpSettings> UpsertAsync(
        SmtpSettings input,
        string? newPassword,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.SmtpSettings
            .FirstOrDefaultAsync(s => s.Id == SingletonId, ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            existing = new SmtpSettings { Id = SingletonId };
            db.SmtpSettings.Add(existing);
        }

        existing.Enabled         = input.Enabled;
        existing.Host            = input.Host.Trim();
        existing.Port            = input.Port;
        existing.TlsMode         = input.TlsMode;
        existing.Username        = string.IsNullOrWhiteSpace(input.Username) ? null : input.Username.Trim();
        existing.FromAddress     = input.FromAddress.Trim();
        existing.FromDisplayName = string.IsNullOrWhiteSpace(input.FromDisplayName) ? null : input.FromDisplayName.Trim();
        existing.TimeoutSeconds  = input.TimeoutSeconds <= 0 ? 30 : input.TimeoutSeconds;

        if (newPassword is null)
        {
            // Preserve existing — operator didn't touch the password field.
        }
        else if (newPassword.Length == 0)
        {
            existing.PasswordEncrypted = null;
        }
        else
        {
            existing.PasswordEncrypted = encryption.Encrypt(newPassword);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Strip the cipher before returning so the caller can hand the row
        // straight back to the UI.
        existing.PasswordEncrypted = null;
        return existing;
    }

    /// <summary>
    /// Result shape for <see cref="SendProbeAsync"/>. <see cref="Succeeded"/>
    /// is the only required field; <see cref="Detail"/> carries either the
    /// server's last response banner (on success) or the exception message
    /// (on failure) — both safe to display verbatim in the UI.
    /// </summary>
    public sealed record ProbeResult(
        bool Succeeded,
        string Detail,
        TimeSpan Elapsed);

    /// <summary>
    /// Sends a one-line probe email to <paramref name="recipient"/> using
    /// the supplied settings (NOT the persisted ones — operators usually
    /// click Test before clicking Save). Returns a result with success +
    /// human-readable detail; does NOT throw.
    /// </summary>
    public async Task<ProbeResult> SendProbeAsync(
        SmtpSettings settings,
        string? passwordOverride,
        string recipient,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipient);
        var started = TimeProvider.System.GetTimestamp();

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(
                settings.FromDisplayName ?? "KrakenDeploy", settings.FromAddress));
            message.To.Add(MailboxAddress.Parse(recipient));
            message.Subject = "KrakenDeploy SMTP test";
            message.Body = new TextPart("plain")
            {
                Text = "This is a test email from KrakenDeploy. " +
                       "If you can read this, your SMTP settings work.\n\n" +
                       $"Sent at {DateTimeOffset.UtcNow:O}.",
            };

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

            if (!string.IsNullOrEmpty(settings.Username) && !string.IsNullOrEmpty(passwordOverride))
            {
                await client.AuthenticateAsync(settings.Username, passwordOverride, ct)
                    .ConfigureAwait(false);
            }

            var response = await client.SendAsync(message, ct).ConfigureAwait(false);
            await client.DisconnectAsync(quit: true, ct).ConfigureAwait(false);

            var elapsed = TimeProvider.System.GetElapsedTime(started);
            logger.LogInformation(
                "SMTP probe ok — recipient={Recipient} elapsed={Elapsed} response={Response}",
                recipient, elapsed, response);
            return new ProbeResult(
                Succeeded: true,
                Detail:    string.IsNullOrEmpty(response) ? "Server accepted the message." : response,
                Elapsed:   elapsed);
        }
        catch (Exception ex)
        {
            var elapsed = TimeProvider.System.GetElapsedTime(started);
            logger.LogWarning(ex,
                "SMTP probe failed — recipient={Recipient} elapsed={Elapsed}",
                recipient, elapsed);
            return new ProbeResult(
                Succeeded: false,
                // MailKit error messages identify the failure clearly
                // ("Could not resolve host", "Authentication failed",
                // "The SMTP server does not support STARTTLS"). Surface
                // verbatim — operators recognise them.
                Detail:    ex.Message,
                Elapsed:   elapsed);
        }
    }
}
