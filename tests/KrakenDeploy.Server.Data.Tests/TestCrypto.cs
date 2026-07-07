using KrakenDeploy.Server.Data.Encryption;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Test helper: an <see cref="AesEncryptionService"/> backed by a fixed key,
/// with no DB/DEK bootstrap. Under envelope encryption the production service
/// keys off an <see cref="IDekProvider"/>; tests just want "encrypt with this
/// key", so <see cref="FixedDekProvider"/> treats the supplied base64 key as
/// the DEK directly. Replaces the old <c>AesEncryptionService(base64Key)</c> ctor.
/// </summary>
internal static class TestCrypto
{
    public static AesEncryptionService Service(string base64Key) =>
        new(new FixedDekProvider(base64Key));

    private sealed class FixedDekProvider(string base64Key) : IDekProvider
    {
        private readonly byte[] _dek = Convert.FromBase64String(base64Key);
        public byte[] GetDek() => _dek;
        public Task EnsureDekAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
