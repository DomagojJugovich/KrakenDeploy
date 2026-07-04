using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Security;

/// <summary>
/// The wrapped data-encryption key (DEK) for envelope encryption (M13.D.2).
/// <para>
/// KrakenDeploy encrypts every secret at rest under a random 256-bit DEK; the
/// DEK itself is stored here <b>wrapped</b> — AES-256-GCM-encrypted under the
/// key-encryption key (KEK) that lives in configuration (<c>Encryption:MasterKey</c>).
/// The config key therefore never touches data directly; it only unwraps this
/// row. That indirection is what makes rotation cheap and safe:
/// </para>
/// <list type="bullet">
///   <item><b>KEK rotation</b> = re-wrap this row under a new KEK (no data walk).</item>
///   <item><b>DEK rotation</b> = generate a new DEK, re-encrypt every secret, swap
///     <see cref="WrappedDek"/> — one atomic transaction (incident response).</item>
/// </list>
/// <para>
/// Platform-level row (deliberately NOT <c>ISpaceScoped</c> — like
/// <c>ApiKey</c>/<c>AuditEntry</c>). Single-instance today: exactly one row with
/// <see cref="AccountId"/> = <see langword="null"/> (enforced by a partial unique
/// index). The nullable <see cref="AccountId"/> is reserved so a future per-account
/// DEK slots in with no re-migration.
/// </para>
/// </summary>
public class DataEncryptionKey : AuditableEntity
{
    /// <summary>
    /// Owning business account, or <see langword="null"/> for the single
    /// instance-wide DEK (the only shape today). Reserved for a future
    /// per-account DEK; a partial unique index guarantees at most one null row.
    /// </summary>
    public Guid? AccountId { get; set; }

    /// <summary>
    /// The DEK wrapped by the KEK: <c>AesGcmCipher.Encrypt(kek, base64(dekBytes))</c>
    /// — the same base64 ciphertext shape every other secret uses. Never logged,
    /// redacted from audit snapshots (see <c>AuditLogInterceptor</c>).
    /// </summary>
    public string WrappedDek { get; set; } = "";

    /// <summary>When this DEK was last rotated (a new DEK generated + all data
    /// re-encrypted). Null until the first DEK rotation.</summary>
    public DateTimeOffset? RotatedUtc { get; set; }
}
