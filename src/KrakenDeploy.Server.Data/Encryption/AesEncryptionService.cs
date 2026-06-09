using KrakenDeploy.Contracts.Crypto;
using KrakenDeploy.Server.Core.Domain.Variables;

namespace KrakenDeploy.Server.Data.Encryption;

/// <summary>
/// AES-256-GCM symmetric encryption for sensitive variable values.
/// <para>
/// Wire format: <c>base64(nonce[12] + authTag[16] + ciphertext[n])</c>.
/// The primitive lives in <see cref="AesGcmCipher"/> (in
/// <c>KrakenDeploy.Contracts</c>) so the offline runner shares the exact same
/// format; this service binds it to the server's configured master key.
/// </para>
/// </summary>
public sealed class AesEncryptionService : IEncryptionService
{
    private readonly byte[] _key;

    /// <param name="base64MasterKey">
    /// Base64-encoded 32-byte (256-bit) key.
    /// Generate with: <c>Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))</c>.
    /// </param>
    public AesEncryptionService(string base64MasterKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base64MasterKey);
        _key = Convert.FromBase64String(base64MasterKey);

        if (_key.Length != AesGcmCipher.KeyBytes)
        {
            throw new ArgumentException(
                "Encryption:MasterKey must be a base64-encoded 32-byte (256-bit) key " +
                $"(decoded to {_key.Length} bytes, expected {AesGcmCipher.KeyBytes}).",
                nameof(base64MasterKey));
        }
    }

    /// <inheritdoc/>
    public string Encrypt(string plaintext) => AesGcmCipher.Encrypt(_key, plaintext);

    /// <inheritdoc/>
    public string Decrypt(string ciphertext) => AesGcmCipher.Decrypt(_key, ciphertext);
}
