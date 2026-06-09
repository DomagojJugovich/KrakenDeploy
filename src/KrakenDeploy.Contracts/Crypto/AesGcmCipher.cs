using System.Security.Cryptography;
using System.Text;

namespace KrakenDeploy.Contracts.Crypto;

/// <summary>
/// AES-256-GCM primitive shared by the server's <c>AesEncryptionService</c> and
/// the offline runner. Wire format: <c>base64(nonce[12] + authTag[16] +
/// ciphertext[n])</c> — a randomly generated nonce per encryption guarantees
/// unique ciphertext for identical plaintexts.
/// <para>
/// Extracted into <c>KrakenDeploy.Contracts</c> so the offline runner (in
/// <c>KrakenDeploy.Agent</c>, which cannot reference the server-side data
/// assembly) decrypts <c>plan.enc</c> with the exact same format the server
/// encrypts with. The key MUST be 32 bytes (256-bit).
/// </para>
/// </summary>
public static class AesGcmCipher
{
    /// <summary>Required key length in bytes (AES-256).</summary>
    public const int KeyBytes = 32;

    private const int NonceBytes = 12; // AES-GCM standard nonce size
    private const int TagBytes = 16;   // AES-GCM authentication tag size

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> with <paramref name="key"/> and
    /// returns <c>base64(nonce + tag + ciphertext)</c>.
    /// </summary>
    public static string Encrypt(byte[] key, string plaintext)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(plaintext);
        EnsureKey(key);

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagBytes];

        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        var combined = new byte[NonceBytes + TagBytes + ciphertext.Length];
        nonce.CopyTo(combined, 0);
        tag.CopyTo(combined, NonceBytes);
        ciphertext.CopyTo(combined, NonceBytes + TagBytes);

        return Convert.ToBase64String(combined);
    }

    /// <summary>
    /// Decrypts a value produced by <see cref="Encrypt"/>. Throws
    /// <see cref="CryptographicException"/> if the input is malformed or tampered.
    /// </summary>
    public static string Decrypt(byte[] key, string base64)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(base64);
        EnsureKey(key);

        var combined = Convert.FromBase64String(base64);
        if (combined.Length < NonceBytes + TagBytes)
        {
            throw new CryptographicException(
                "Ciphertext is too short to contain a valid nonce and authentication tag.");
        }

        var nonce = combined.AsSpan(0, NonceBytes);
        var tag = combined.AsSpan(NonceBytes, TagBytes);
        var encrypted = combined.AsSpan(NonceBytes + TagBytes);
        var decrypted = new byte[encrypted.Length];

        using var aes = new AesGcm(key, TagBytes);
        aes.Decrypt(nonce, encrypted, tag, decrypted);

        return Encoding.UTF8.GetString(decrypted);
    }

    private static void EnsureKey(byte[] key)
    {
        if (key.Length != KeyBytes)
        {
            throw new ArgumentException(
                $"AES-256-GCM key must be {KeyBytes} bytes (got {key.Length}).", nameof(key));
        }
    }
}
