using KrakenDeploy.Server.Core.Domain.Settings;

namespace KrakenDeploy.Server.Core.Domain.Notifications;

/// <summary>
/// Server-wide SMTP configuration for outbound notifications (M13.B.1).
/// A System-scoped <see cref="ISettingsDocument"/> (key <c>"smtp"</c>) stored
/// in the unified <c>settings</c> table. M13.B.2 event subscriptions consume
/// these settings to deliver per-event email notifications.
///
/// <para>
/// Storage: the password is AES-256-GCM ciphertext produced by
/// <c>IEncryptionService</c> (same primitive as <c>SpaceAiSettings.ApiKeyEncrypted</c>
/// and sensitive variables). It MUST NOT cross to the browser as ciphertext
/// — settings GET returns <see langword="null"/> for the encrypted field,
/// the UI shows a "leave blank to keep current" affordance, and PUT only
/// re-encrypts when a new value is supplied.
/// </para>
/// </summary>
public class SmtpSettings : ISettingsDocument
{
    /// <inheritdoc />
    public static string Key => "smtp";

    /// <inheritdoc />
    public static SettingsScope Scope => SettingsScope.System;

    /// <summary>
    /// Master switch. When false the settings persist but the notification
    /// pipeline silently no-ops — useful for "draft this config, don't send
    /// yet" workflows during onboarding.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>SMTP server hostname or IP (e.g. <c>smtp.gmail.com</c>).</summary>
    public string Host { get; set; } = "";

    /// <summary>Port. Common: 25 (plaintext / opportunistic STARTTLS),
    /// 465 (implicit TLS), 587 (submission with STARTTLS).</summary>
    public int Port { get; set; } = 587;

    /// <summary>How the client negotiates TLS — see <see cref="SmtpTlsMode"/>.</summary>
    public SmtpTlsMode TlsMode { get; set; } = SmtpTlsMode.StartTlsWhenAvailable;

    /// <summary>Authentication username (often the same as the From address,
    /// or a relay-account login). Null = anonymous (some internal relays).</summary>
    public string? Username { get; set; }

    /// <summary>AES-256-GCM ciphertext of the password. Null = anonymous /
    /// no password set. Never serialised to JSON, never logged.</summary>
    public string? PasswordEncrypted { get; set; }

    /// <summary>
    /// From address. RFC 5321 envelope sender + display From header.
    /// Required when <see cref="Enabled"/> = true (gateways reject anonymous
    /// envelopes from unauthenticated submitters).
    /// </summary>
    public string FromAddress { get; set; } = "";

    /// <summary>Optional display name on the From header, e.g.
    /// "KrakenDeploy". Many SMTP gateways accept this verbatim.</summary>
    public string? FromDisplayName { get; set; }

    /// <summary>
    /// Connect / send timeout in seconds. Default 30 s — longer than typical
    /// SMTP RTTs but short enough that a hanging relay doesn't block a
    /// background job for minutes.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>TLS negotiation mode for outbound SMTP connections.</summary>
public enum SmtpTlsMode
{
    /// <summary>No TLS — plaintext. Only acceptable for trusted internal
    /// relays on a private network (port 25 inside a DC perimeter).</summary>
    None = 0,

    /// <summary>
    /// Try STARTTLS if the server advertises it; fall back to plaintext
    /// otherwise. <em>Not</em> recommended for the public internet because
    /// a downgrade attacker can strip STARTTLS — use <see cref="StartTlsRequired"/>
    /// for any relay outside the trusted perimeter.
    /// </summary>
    StartTlsWhenAvailable = 1,

    /// <summary>STARTTLS required; refuse to send if the server doesn't
    /// advertise it or the upgrade fails.</summary>
    StartTlsRequired = 2,

    /// <summary>Implicit TLS — wrap the TCP socket in TLS from byte 0
    /// (typically port 465).</summary>
    ImplicitTls = 3,
}
