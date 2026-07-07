using System.Security.Cryptography;
using KrakenDeploy.Contracts.Crypto;
using KrakenDeploy.Server.Core.Domain.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KrakenDeploy.Server.Data.Encryption;

/// <summary>
/// Envelope-encryption key custody (M13.D.2). Holds the KEK (from
/// <c>Encryption:MasterKey</c>) and the unwrapped DEK, and mediates between the
/// singleton <see cref="AesEncryptionService"/> and the DB-resident wrapped DEK.
/// <para>
/// A singleton can't inject a scoped <c>KrakenDbContext</c>, so — like
/// <c>LicenseUsageCounter</c> — it opens a short scope via
/// <see cref="IServiceScopeFactory"/> to read the wrapped row, unwraps it with
/// the KEK, and caches the raw 32-byte DEK behind a lock for the process
/// lifetime (the DEK only ever changes via an offline rotation, server stopped).
/// </para>
/// </summary>
public sealed class DekProvider : IDekProvider
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly byte[] _kek;
    private readonly Lock _gate = new();
    private byte[]? _dek;

    public DekProvider(IServiceScopeFactory scopeFactory, string kekBase64)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(kekBase64);
        _scopeFactory = scopeFactory;
        _kek = Convert.FromBase64String(kekBase64);
        if (_kek.Length != AesGcmCipher.KeyBytes)
        {
            throw new ArgumentException(
                "Encryption:MasterKey (the KEK) must be a base64-encoded 32-byte key " +
                $"(decoded to {_kek.Length} bytes, expected {AesGcmCipher.KeyBytes}).",
                nameof(kekBase64));
        }
    }

    /// <summary>
    /// The unwrapped 32-byte DEK. Loads + caches on first use (sync EF query in a
    /// fresh scope). Throws if no DEK row exists (bootstrap never ran) or the KEK
    /// can't unwrap it (wrong <c>Encryption:MasterKey</c>).
    /// </summary>
    public byte[] GetDek()
    {
        var cached = Volatile.Read(ref _dek);
        if (cached is not null)
        {
            return cached;
        }

        lock (_gate)
        {
            if (_dek is not null)
            {
                return _dek;
            }

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();
            var row = db.DataEncryptionKeys.AsNoTracking()
                .FirstOrDefault(k => k.AccountId == null)
                ?? throw new InvalidOperationException(
                    "No data-encryption key (DEK) has been provisioned. Run 'database setup' " +
                    "(or start the server in Development) to generate one before encrypting secrets.");

            _dek = Unwrap(_kek, row.WrappedDek);
            return _dek;
        }
    }

    /// <summary>
    /// Idempotent first-boot bootstrap: generates + persists a wrapped DEK if
    /// none exists, then eagerly unwraps + caches it (fail-fast on a wrong KEK).
    /// Safe to call on every boot; a partial unique index makes a concurrent
    /// double-insert impossible. Call after <c>Database.Migrate</c>.
    /// </summary>
    public async Task EnsureDekAsync(CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();

        var row = await db.DataEncryptionKeys
            .FirstOrDefaultAsync(k => k.AccountId == null, ct).ConfigureAwait(false);

        if (row is null)
        {
            var dek = RandomNumberGenerator.GetBytes(AesGcmCipher.KeyBytes);
            db.DataEncryptionKeys.Add(new DataEncryptionKey
            {
                AccountId = null,
                WrappedDek = Wrap(_kek, dek),
            });
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                // Lost a concurrent bootstrap race (partial unique index). The
                // winner's row is authoritative — fall through and load it.
                row = await db.DataEncryptionKeys.AsNoTracking()
                    .FirstOrDefaultAsync(k => k.AccountId == null, ct).ConfigureAwait(false);
            }
        }

        // Eager unwrap + cache: surfaces a wrong-KEK error now (at boot), not on
        // the first secret access mid-request.
        var wrapped = row?.WrappedDek
            ?? (await db.DataEncryptionKeys.AsNoTracking()
                .FirstAsync(k => k.AccountId == null, ct).ConfigureAwait(false)).WrappedDek;
        var unwrapped = Unwrap(_kek, wrapped);
        lock (_gate)
        {
            _dek = unwrapped;
        }
    }

    /// <summary>Wrap a raw DEK under a KEK: base64 of the DEK, AES-GCM-encrypted.
    /// Same ciphertext shape as every other secret (and the offline-drop
    /// bundle/HMAC keys), so no format special-casing.</summary>
    public static string Wrap(byte[] kek, byte[] dek) =>
        AesGcmCipher.Encrypt(kek, Convert.ToBase64String(dek));

    /// <summary>Unwrap a wrapped DEK with a KEK. Throws
    /// <see cref="CryptographicException"/> if the KEK is wrong (GCM tag fails).</summary>
    public static byte[] Unwrap(byte[] kek, string wrappedDek) =>
        Convert.FromBase64String(AesGcmCipher.Decrypt(kek, wrappedDek));
}
