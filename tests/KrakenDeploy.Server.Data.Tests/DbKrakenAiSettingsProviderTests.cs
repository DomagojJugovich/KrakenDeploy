using FluentAssertions;
using KrakenDeploy.Ai;
using KrakenDeploy.Server.Core.Domain.Ai;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Spaces;
using KrakenDeploy.Server.Data.Encryption;
using KrakenDeploy.Server.Data.Services.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Integration tests for the M11.A.6.2 settings provider. Run against the
/// shared Postgres fixture so the global query filter + auditable +
/// space-scoping interceptors all execute — same path the wrapper hits
/// at request time.
/// <para>
/// Each test cleans up the SpaceAiSettings table on entry so individual
/// cases stay independent (one row per Space is enforced by the unique
/// index — leaving rows around would fail subsequent inserts).
/// </para>
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class DbKrakenAiSettingsProviderTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateContext();
        await db.SpaceAiSettings.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GetAsync_returns_disabled_when_no_row_exists_for_current_space()
    {
        var provider = NewProvider(WellKnown.DefaultSpaceId);

        var settings = await provider.GetAsync();

        settings.Provider.Should().Be(KrakenAiProvider.Disabled);
        settings.ApiKey.Should().BeNull();
        settings.Model.Should().BeNull();
        settings.Features.DiagnosisEnabled.Should().BeFalse();
        settings.Features.McpEnabled.Should().BeFalse();
        settings.Features.AdhocEnabled.Should().BeFalse();
        settings.Features.AssistantEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task GetAsync_returns_disabled_when_no_ambient_space()
    {
        // Background-job paths that haven't WithSpace'd yet get the
        // default-disabled record. The wrapper short-circuits with
        // KrakenAiDisabledException — no LLM contact.
        var provider = NewProvider(spaceId: Guid.Empty);

        var settings = await provider.GetAsync();

        settings.Provider.Should().Be(KrakenAiProvider.Disabled);
    }

    [Fact]
    public async Task GetAsync_projects_row_correctly_and_decrypts_api_key()
    {
        var encryption  = NewEncryptionService();
        var encryptedKey = encryption.Encrypt("sk-real-key-not-actually-real");
        await SeedSettingsAsync(new SpaceAiSettings
        {
            SpaceId           = WellKnown.DefaultSpaceId,
            Provider          = KrakenAiProviderValue.Anthropic,
            Model             = "claude-sonnet-4.6",
            ApiKeyEncrypted   = encryptedKey,
            BaseUrl           = null,
            BudgetUsdPerMonth = 100m,
            LogPromptBodies   = false,
            DiagnosisEnabled  = true,
            McpEnabled        = false,
            AdhocEnabled      = true,
            AssistantEnabled  = false,
        });

        var provider = NewProvider(WellKnown.DefaultSpaceId, encryption);
        var settings = await provider.GetAsync();

        settings.Provider.Should().Be(KrakenAiProvider.Anthropic);
        settings.Model.Should().Be("claude-sonnet-4.6");
        settings.ApiKey.Should().Be("sk-real-key-not-actually-real",
            "the provider decrypts the ciphertext on the request path");
        settings.BudgetUsdPerMonth.Should().Be(100m);
        settings.Features.DiagnosisEnabled.Should().BeTrue();
        settings.Features.McpEnabled.Should().BeFalse();
        settings.Features.AdhocEnabled.Should().BeTrue();
        settings.Features.AssistantEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task GetAsync_handles_null_ApiKeyEncrypted_without_throwing()
    {
        // Operator created settings row but hasn't pasted a key yet. The
        // provider returns ApiKey=null; the wrapper's factory rejects
        // empty-key configurations downstream.
        await SeedSettingsAsync(new SpaceAiSettings
        {
            SpaceId           = WellKnown.DefaultSpaceId,
            Provider          = KrakenAiProviderValue.Anthropic,
            Model             = "claude-sonnet-4.6",
            ApiKeyEncrypted   = null,
            BudgetUsdPerMonth = 0m,
        });

        var settings = await NewProvider(WellKnown.DefaultSpaceId).GetAsync();

        settings.Provider.Should().Be(KrakenAiProvider.Anthropic);
        settings.ApiKey.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_treats_unknown_provider_string_as_Disabled()
    {
        // A stale row from a downgraded binary (e.g. a provider added in a
        // newer release) must not crash AI on read.
        await SeedSettingsAsync(new SpaceAiSettings
        {
            SpaceId           = WellKnown.DefaultSpaceId,
            Provider          = "FutureProviderNotYetReleased",
            BudgetUsdPerMonth = 0m,
        });

        var settings = await NewProvider(WellKnown.DefaultSpaceId).GetAsync();

        settings.Provider.Should().Be(KrakenAiProvider.Disabled,
            "an unparseable provider string must not crash AI — log + degrade");
    }

    [Fact]
    public async Task Settings_isolated_across_spaces_by_global_query_filter()
    {
        // Pins the contract that the global query filter on ISpaceScoped
        // restricts reads to the DbContext's ambient Space. We seed TWO
        // rows — one under DefaultSpaceId, one under a foreign Space —
        // and prove the provider only ever sees the DefaultSpaceId row
        // (which is what the test fixture's ISpaceContext pins).
        var foreignSpaceId = Guid.NewGuid();
        var encryption = NewEncryptionService();
        await SeedSettingsAsync(new SpaceAiSettings
        {
            SpaceId           = WellKnown.DefaultSpaceId,
            Provider          = KrakenAiProviderValue.Anthropic,
            Model             = "claude-MINE",
            ApiKeyEncrypted   = encryption.Encrypt("key-mine"),
            BudgetUsdPerMonth = 100m,
        });
        await SeedSettingsAsync(new SpaceAiSettings
        {
            SpaceId           = foreignSpaceId,
            Provider          = KrakenAiProviderValue.OpenAI,
            Model             = "gpt-NOT-MINE",
            ApiKeyEncrypted   = encryption.Encrypt("key-foreign"),
            BudgetUsdPerMonth = 50m,
        });

        var settings = await NewProvider(WellKnown.DefaultSpaceId, encryption).GetAsync();

        settings.Provider.Should().Be(KrakenAiProvider.Anthropic);
        settings.Model.Should().Be("claude-MINE",
            "the global query filter restricts reads to the current Space — " +
            "the foreign row must not be visible");
        settings.ApiKey.Should().Be("key-mine");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private DbKrakenAiSettingsProvider NewProvider(
        Guid spaceId, AesEncryptionService? encryption = null) =>
        new(
            postgres,
            new FixedSpaceContext(spaceId),
            encryption ?? NewEncryptionService(),
            NullLogger<DbKrakenAiSettingsProvider>.Instance);

    private static AesEncryptionService NewEncryptionService() =>
        new(Convert.ToBase64String(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));

    private async Task SeedSettingsAsync(SpaceAiSettings row)
    {
        await using var db = postgres.CreateContext();
        db.SpaceAiSettings.Add(row);
        await db.SaveChangesAsync();
    }

    private sealed class FixedSpaceContext(Guid spaceId) : ISpaceContext
    {
        public Guid CurrentSpaceId => spaceId;
        public IDisposable WithSpace(Guid newSpaceId) => new NoOpDisposable();
        private sealed class NoOpDisposable : IDisposable { public void Dispose() { } }
    }
}
