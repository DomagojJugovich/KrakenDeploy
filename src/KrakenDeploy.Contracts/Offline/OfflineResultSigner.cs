using System.Security.Cryptography;
using System.Text;

namespace KrakenDeploy.Contracts.Offline;

/// <summary>
/// HMAC-SHA256 integrity for the offline runner's <c>deployment-result.json</c>.
/// The result drives server-side DB writes (status, per-step outcomes, output
/// variables) on upload, so it must be protected against tampering in transit
/// (webhook / file-share / email return channels).
/// <para>
/// Keyed off the per-target bundle key: the runner holds it (to decrypt the
/// plan) and the server holds it (encrypted on the target), so both can derive
/// the same signing key with no extra key distribution. A labelled sub-key is
/// derived rather than using the AES bundle key directly for HMAC.
/// </para>
/// </summary>
public static class OfflineResultSigner
{
    private static readonly byte[] DerivationLabel =
        Encoding.UTF8.GetBytes("kraken-offline-result-v1");

    /// <summary>Computes the result signature over <paramref name="resultBytes"/>.</summary>
    public static byte[] Sign(byte[] bundleKey, byte[] resultBytes)
    {
        ArgumentNullException.ThrowIfNull(bundleKey);
        ArgumentNullException.ThrowIfNull(resultBytes);
        using var sig = new HMACSHA256(DeriveKey(bundleKey));
        return sig.ComputeHash(resultBytes);
    }

    /// <summary>Constant-time verification of a signature from <see cref="Sign"/>.</summary>
    public static bool Verify(byte[] bundleKey, byte[] resultBytes, byte[] signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        var expected = Sign(bundleKey, resultBytes);
        return CryptographicOperations.FixedTimeEquals(expected, signature);
    }

    private static byte[] DeriveKey(byte[] bundleKey)
    {
        using var kdf = new HMACSHA256(bundleKey);
        return kdf.ComputeHash(DerivationLabel);
    }
}
