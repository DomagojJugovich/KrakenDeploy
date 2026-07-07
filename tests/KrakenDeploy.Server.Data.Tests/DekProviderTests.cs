using System.Security.Cryptography;
using FluentAssertions;
using KrakenDeploy.Contracts.Crypto;
using KrakenDeploy.Server.Data.Encryption;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Unit tests for <see cref="DekProvider"/> (M13.D.2): idempotent first-boot
/// generation, wrong-KEK fail-fast, and the clear error when no DEK exists.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class DekProviderTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateContext();
        await db.DataEncryptionKeys.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string RandKek() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(AesGcmCipher.KeyBytes));

    [Fact]
    public async Task EnsureDek_generates_one_row_idempotently_and_a_second_provider_unwraps_the_same_DEK()
    {
        var kek = RandKek();
        var provider = new DekProvider(postgres.ScopeFactory, kek);

        await provider.EnsureDekAsync();
        var dek = provider.GetDek();
        dek.Length.Should().Be(AesGcmCipher.KeyBytes);

        // Idempotent: a second call must NOT create a second row.
        await provider.EnsureDekAsync();
        await using (var db = postgres.CreateContext())
        {
            (await db.DataEncryptionKeys.CountAsync(k => k.AccountId == null)).Should().Be(1);
        }

        // A fresh provider with the SAME KEK unwraps the identical DEK.
        new DekProvider(postgres.ScopeFactory, kek).GetDek().Should().Equal(dek);
    }

    [Fact]
    public async Task Wrong_KEK_fails_fast_on_both_GetDek_and_EnsureDek()
    {
        await new DekProvider(postgres.ScopeFactory, RandKek()).EnsureDekAsync();

        var wrong = new DekProvider(postgres.ScopeFactory, RandKek());
        ((Action)(() => wrong.GetDek()))
            .Should().Throw<CryptographicException>("the wrong KEK can't unwrap the DEK (GCM tag fails)");

        var wrong2 = new DekProvider(postgres.ScopeFactory, RandKek());
        await wrong2.Invoking(p => p.EnsureDekAsync())
            .Should().ThrowAsync<CryptographicException>("EnsureDek eagerly verifies the existing DEK");
    }

    [Fact]
    public void GetDek_without_a_provisioned_DEK_throws_a_clear_error()
    {
        // Table cleared in Init; no EnsureDek called.
        var provider = new DekProvider(postgres.ScopeFactory, RandKek());
        ((Action)(() => provider.GetDek()))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*No data-encryption key*");
    }

    [Fact]
    public void Ctor_rejects_a_non_32_byte_KEK()
    {
        var shortKek = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        ((Action)(() => { _ = new DekProvider(postgres.ScopeFactory, shortKek); }))
            .Should().Throw<ArgumentException>().WithMessage("*32-byte*");
    }
}
