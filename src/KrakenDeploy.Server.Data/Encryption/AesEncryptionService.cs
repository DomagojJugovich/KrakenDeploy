using KrakenDeploy.Contracts.Crypto;
using KrakenDeploy.Server.Core.Domain.Variables;

namespace KrakenDeploy.Server.Data.Encryption;

/// <summary>
/// AES-256-GCM encryption for secrets at rest, under ENVELOPE encryption
/// (M13.D.2): data is encrypted with the data-encryption key (DEK) supplied by
/// <see cref="IDekProvider"/>, and the DEK itself is wrapped by the KEK
/// (config <c>Encryption:MasterKey</c>). The config key never touches data
/// directly — it only unwraps the DEK — so rotating it (KEK rotation) is a
/// cheap re-wrap, and rotating the DEK re-encrypts all data in one pass.
/// <para>
/// Wire format is unchanged: <c>base64(nonce[12] + authTag[16] + ciphertext[n])</c>
/// via <see cref="AesGcmCipher"/> — only the key bytes now come from the DEK.
/// The <see cref="IEncryptionService"/> surface stays synchronous; the DEK is
/// cached in <see cref="IDekProvider"/>, so these calls don't hit the DB.
/// </para>
/// </summary>
public sealed class AesEncryptionService(IDekProvider dek) : IEncryptionService
{
    /// <inheritdoc/>
    public string Encrypt(string plaintext) => AesGcmCipher.Encrypt(dek.GetDek(), plaintext);

    /// <inheritdoc/>
    public string Decrypt(string ciphertext) => AesGcmCipher.Decrypt(dek.GetDek(), ciphertext);
}
