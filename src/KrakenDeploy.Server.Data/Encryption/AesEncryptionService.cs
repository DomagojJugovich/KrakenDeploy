using System.Security.Cryptography;
using System.Text;
using KrakenDeploy.Server.Core.Domain.Variables;

namespace KrakenDeploy.Server.Data.Encryption;

/// <summary>
/// AES-256-GCM symmetric encryption for sensitive variable values.
/// <para>
/// Wire format: <c>base64(nonce[12] + authTag[16] + ciphertext[n])</c>.
/// The nonce is randomly generated per encryption to guarantee ciphertext
/// uniqueness even for identical plaintexts.
/// </para>
/// </summary>
public sealed class AesEncryptionService : IEncryptionService
{
    private const int NonceBytes = 12;  // AES-GCM standard nonce size
    private const int TagBytes = 16;    // AES-GCM authentication tag size

    private readonly byte[] _key;

    /// <param name="base64MasterKey">
    /// Base64-encoded 32-byte (256-bit) key.
    /// Generate with: <c>Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))</c>.
    /// </param>
    public AesEncryptionService(string base64MasterKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base64MasterKey);
        _key = Convert.FromBase64String(base64MasterKey);

        if (_key.Length != 32)
        {
            throw new ArgumentException(
                "Encryption:MasterKey must be a base64-encoded 32-byte (256-bit) key " +
                $"(decoded to {_key.Length} bytes, expected 32).",
                nameof(base64MasterKey));
        }
    }

    /// <inheritdoc/>
    public string Encrypt(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagBytes];

        using var aes = new AesGcm(_key, TagBytes);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // Serialize as nonce + tag + ciphertext — all three are needed to decrypt.
        var combined = new byte[NonceBytes + TagBytes + ciphertext.Length];
        nonce.CopyTo(combined, 0);
        tag.CopyTo(combined, NonceBytes);
        ciphertext.CopyTo(combined, NonceBytes + TagBytes);

        return Convert.ToBase64String(combined);
    }

    /// <inheritdoc/>
    public string Decrypt(string ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);

        var combined = Convert.FromBase64String(ciphertext);

        if (combined.Length < NonceBytes + TagBytes)
        {
            throw new CryptographicException(
                "Ciphertext is too short to contain a valid nonce and authentication tag.");
        }

        var nonce = combined.AsSpan(0, NonceBytes);
        var tag = combined.AsSpan(NonceBytes, TagBytes);
        var encrypted = combined.AsSpan(NonceBytes + TagBytes);

        var decrypted = new byte[encrypted.Length];

        using var aes = new AesGcm(_key, TagBytes);
        aes.Decrypt(nonce, encrypted, tag, decrypted);

        return Encoding.UTF8.GetString(decrypted);
    }
}
