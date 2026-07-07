namespace KrakenDeploy.Server.Data.Encryption;

/// <summary>
/// Supplies the unwrapped data-encryption key (DEK) to
/// <see cref="AesEncryptionService"/> under envelope encryption (M13.D.2).
/// The KEK (config <c>Encryption:MasterKey</c>) unwraps the DB-resident DEK;
/// data is encrypted under the DEK.
/// </summary>
public interface IDekProvider
{
    /// <summary>The unwrapped 32-byte DEK (cached after first load). Throws if
    /// no DEK is provisioned or the KEK can't unwrap it.</summary>
    byte[] GetDek();

    /// <summary>Idempotently generate + persist a wrapped DEK if none exists,
    /// then eagerly cache it (fail-fast on a wrong KEK). Call after migrate.</summary>
    Task EnsureDekAsync(CancellationToken ct = default);
}
