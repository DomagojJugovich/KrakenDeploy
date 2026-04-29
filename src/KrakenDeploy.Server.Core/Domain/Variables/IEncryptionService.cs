namespace KrakenDeploy.Server.Core.Domain.Variables;

/// <summary>
/// Symmetric encryption service for sensitive variable values.
/// Implemented by <c>AesEncryptionService</c> (AES-256-GCM) in <c>KrakenDeploy.Server.Data</c>.
/// </summary>
public interface IEncryptionService
{
    /// <summary>
    /// Encrypts <paramref name="plaintext"/> and returns a base64-encoded
    /// ciphertext that includes the nonce and auth tag.
    /// </summary>
    string Encrypt(string plaintext);

    /// <summary>
    /// Decrypts a ciphertext produced by <see cref="Encrypt"/>.
    /// Throws <see cref="CryptographicException"/> if the ciphertext is tampered.
    /// </summary>
    string Decrypt(string ciphertext);
}
