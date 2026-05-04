namespace KrakenDeploy.Server.Core.Domain.Targets;

/// <summary>
/// Configuration for offline drop targets. Stored as JSONB on <see cref="DeploymentTarget"/>.
/// Sensitive values (passwords, secrets, HMAC key) are encrypted via
/// <c>AesEncryptionService</c> before being stored.
/// </summary>
public class OfflineDropConfig
{
    /// <summary>How the drop bundle is delivered to the target environment.</summary>
    public OfflineDropDeliveryChannel DeliveryChannel { get; set; } =
        OfflineDropDeliveryChannel.Manual;

    /// <summary>
    /// Base64-encoded 32-byte HMAC-SHA256 key, encrypted with the server's master key.
    /// Used to sign <c>manifest.json</c> in both drop and result bundles.
    /// </summary>
    public string? HmacKeyEncrypted { get; set; }

    // ── Email delivery ────────────────────────────────────────────────────────

    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseSsl { get; set; } = true;
    public string? SmtpUsername { get; set; }

    /// <summary>SMTP password encrypted with the server's master key.</summary>
    public string? SmtpPasswordEncrypted { get; set; }

    /// <summary>Email address to receive the drop bundle.</summary>
    public string? SmtpRecipient { get; set; }

    /// <summary>Sender address for the drop bundle email.</summary>
    public string? SmtpSender { get; set; }

    // ── Webhook delivery ──────────────────────────────────────────────────────

    /// <summary>URL to POST the drop bundle to.</summary>
    public string? WebhookUrl { get; set; }

    /// <summary>Optional shared secret for webhook HMAC verification (encrypted).</summary>
    public string? WebhookSecretEncrypted { get; set; }

    // ── File-share / SFTP delivery ────────────────────────────────────────────

    /// <summary>
    /// UNC path (e.g. <c>\\server\share\drops</c>) or SFTP host for file-share delivery.
    /// </summary>
    public string? FileSharePath { get; set; }

    /// <summary>Optional username for file-share/SFTP authentication.</summary>
    public string? FileShareUsername { get; set; }

    /// <summary>Password for file-share/SFTP authentication (encrypted).</summary>
    public string? FileSharePasswordEncrypted { get; set; }
}
