using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Ai;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Spaces;
using KrakenDeploy.Server.Data.Encryption;
using KrakenDeploy.Server.Data.Services.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Integration tests for the M11.A.6.3 CRUD service. The service is the
/// authoritative implementation of the API-key preserve/clear/change
/// semantics that the REST endpoints rely on — pin it here so a refactor
/// can't silently break the "operator edits Model without re-pasting the
/// key" workflow.
/// </summary>
[Collection("Postgres")]
public sealed class SpaceAiSettingsServiceTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateContext();
        await db.SpaceAiSettings.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GetAsync_returns_default_dto_when_no_row()
    {
        var svc = NewSvc();
        var dto = await svc.GetAsync();

        dto.Provider.Should().Be(KrakenAiProviderValue.Disabled);
        dto.HasApiKey.Should().BeFalse();
        dto.ApiKeyMasked.Should().BeNull();
        dto.DiagnosisEnabled.Should().BeFalse();
        dto.AdhocMaxIterations.Should().Be(5,
            "the default-shaped DTO must report the documented default cap");
        dto.AdhocTwoPersonApproval.Should().BeFalse(
            "two-person approval is opt-in; default off");
    }

    [Fact]
    public async Task UpdateAsync_creates_row_lazily_on_first_save()
    {
        var svc = NewSvc();
        var dto = await svc.UpdateAsync(new UpdateSpaceAiSettingsRequest
        {
            Provider          = KrakenAiProviderValue.Anthropic,
            Model             = "claude-sonnet-4.6",
            ApiKey            = "sk-test-key-not-real",
            BudgetUsdPerMonth = 100m,
            DiagnosisEnabled  = true,
        });

        dto.Provider.Should().Be(KrakenAiProviderValue.Anthropic);
        dto.HasApiKey.Should().BeTrue();
        dto.ApiKeyMasked.Should().StartWith("••••••••");
        dto.ApiKeyMasked.Should().NotContain("sk-test",
            "the masked DTO must not leak any of the plaintext key");

        // Confirms one row exists for this Space.
        await using var db = postgres.CreateContext();
        var count = await db.SpaceAiSettings.IgnoreQueryFilters().CountAsync();
        count.Should().Be(1);
    }

    [Fact]
    public async Task UpdateAsync_with_blank_ApiKey_preserves_existing_ciphertext()
    {
        var svc = NewSvc();
        await svc.UpdateAsync(new UpdateSpaceAiSettingsRequest
        {
            Provider          = KrakenAiProviderValue.Anthropic,
            Model             = "claude-sonnet-4.6",
            ApiKey            = "original-secret-key",
            BudgetUsdPerMonth = 100m,
        });
        var maskedBefore = (await svc.GetAsync()).ApiKeyMasked;

        // Edit only the Model — leave ApiKey blank.
        await svc.UpdateAsync(new UpdateSpaceAiSettingsRequest
        {
            Provider          = KrakenAiProviderValue.Anthropic,
            Model             = "claude-haiku-4.5",  // changed
            ApiKey            = null,                 // blank → preserve
            BudgetUsdPerMonth = 100m,
        });

        var dto = await svc.GetAsync();
        dto.Model.Should().Be("claude-haiku-4.5");
        dto.HasApiKey.Should().BeTrue("the key must have survived the partial edit");
        dto.ApiKeyMasked.Should().Be(maskedBefore,
            "the mask must be byte-identical — same ciphertext, same suffix");

        // Round-trip via reveal to prove the actual plaintext is preserved.
        var revealed = await svc.RevealApiKeyAsync();
        revealed.Should().Be("original-secret-key");
    }

    [Fact]
    public async Task UpdateAsync_with_CLEAR_sentinel_removes_the_api_key()
    {
        var svc = NewSvc();
        await svc.UpdateAsync(new UpdateSpaceAiSettingsRequest
        {
            Provider = KrakenAiProviderValue.Anthropic,
            ApiKey   = "to-be-cleared",
        });
        (await svc.GetAsync()).HasApiKey.Should().BeTrue();

        await svc.UpdateAsync(new UpdateSpaceAiSettingsRequest
        {
            Provider = KrakenAiProviderValue.Anthropic,
            ApiKey   = SpaceAiSettingsService.ApiKeyClearSentinel,
        });

        var dto = await svc.GetAsync();
        dto.HasApiKey.Should().BeFalse(
            "the explicit clear sentinel must nullify the ciphertext column");
        dto.ApiKeyMasked.Should().BeNull();
        (await svc.RevealApiKeyAsync()).Should().BeNull();
    }

    [Fact]
    public async Task UpdateAsync_with_new_ApiKey_re_encrypts_and_replaces()
    {
        var svc = NewSvc();
        await svc.UpdateAsync(new UpdateSpaceAiSettingsRequest
        {
            Provider = KrakenAiProviderValue.Anthropic,
            ApiKey   = "first-key",
        });
        var firstMask = (await svc.GetAsync()).ApiKeyMasked;

        await svc.UpdateAsync(new UpdateSpaceAiSettingsRequest
        {
            Provider = KrakenAiProviderValue.Anthropic,
            ApiKey   = "second-different-key",
        });
        var secondMask = (await svc.GetAsync()).ApiKeyMasked;

        secondMask.Should().NotBe(firstMask,
            "new ciphertext → new suffix; operators see 'key was changed'");
        (await svc.RevealApiKeyAsync()).Should().Be("second-different-key");
    }

    [Fact]
    public async Task UpdateAsync_persists_AdhocMaxIterations_round_trip()
    {
        var svc = NewSvc();
        await svc.UpdateAsync(new UpdateSpaceAiSettingsRequest
        {
            Provider               = KrakenAiProviderValue.Anthropic,
            AdhocEnabled           = true,
            AdhocMaxIterations     = 8,
            AdhocTwoPersonApproval = true,
        });

        var dto = await svc.GetAsync();
        dto.AdhocMaxIterations.Should().Be(8,
            "the per-Space cap must survive the write/read round-trip");
        dto.AdhocTwoPersonApproval.Should().BeTrue(
            "the two-person opt-in must survive the write/read round-trip");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(21)]
    public async Task UpdateAsync_rejects_out_of_range_AdhocMaxIterations(int value)
    {
        var svc = NewSvc();
        var act = () => svc.UpdateAsync(new UpdateSpaceAiSettingsRequest
        {
            Provider           = KrakenAiProviderValue.Anthropic,
            AdhocMaxIterations = value,
        });
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*AdhocMaxIterations*");
    }

    [Fact]
    public async Task UpdateAsync_rejects_negative_budget()
    {
        var svc = NewSvc();
        var act = () => svc.UpdateAsync(new UpdateSpaceAiSettingsRequest
        {
            Provider          = KrakenAiProviderValue.Disabled,
            BudgetUsdPerMonth = -1m,
        });
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Budget*");
    }

    [Fact]
    public async Task UpdateAsync_rejects_malformed_base_url()
    {
        var svc = NewSvc();
        var act = () => svc.UpdateAsync(new UpdateSpaceAiSettingsRequest
        {
            Provider = KrakenAiProviderValue.LocalOpenAiCompatible,
            BaseUrl  = "not-a-url",
        });
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*BaseUrl*");
    }

    [Fact]
    public async Task RevealApiKeyAsync_returns_null_when_no_key_configured()
    {
        var svc = NewSvc();
        await svc.UpdateAsync(new UpdateSpaceAiSettingsRequest
        {
            Provider = KrakenAiProviderValue.Disabled,
            ApiKey   = null,
        });

        var revealed = await svc.RevealApiKeyAsync();
        revealed.Should().BeNull(
            "reveal must distinguish 'no key' from a thrown exception so " +
            "the UI can render a clear 'no key configured' state");
    }

    [Fact]
    public async Task GetUsageAsync_returns_zero_when_no_calls_this_month()
    {
        var svc = NewSvc();
        var usage = await svc.GetUsageAsync();

        usage.TotalCalls.Should().Be(0);
        usage.TotalCostUsd.Should().Be(0m);
        usage.FeatureBreakdown.Should().BeEmpty();
    }

    [Fact]
    public async Task GetUsageAsync_aggregates_per_feature_for_current_month()
    {
        // Seed three call logs in the current Space's current month —
        // two for Diagnosis, one for Adhoc.
        await using var db = postgres.CreateContext();
        var now = DateTimeOffset.UtcNow;
        db.AiCallLogs.AddRange(
            NewLog(now, "Diagnosis", prompt: 100, completion: 50,  cost: 0.0008m),
            NewLog(now, "Diagnosis", prompt: 200, completion: 80,  cost: 0.0014m),
            NewLog(now, "Adhoc",     prompt: 500, completion: 250, cost: 0.0050m));
        await db.SaveChangesAsync();

        var svc = NewSvc();
        var usage = await svc.GetUsageAsync();

        usage.TotalCalls.Should().Be(3);
        usage.TotalCostUsd.Should().Be(0.0072m);
        usage.FeatureBreakdown.Should().HaveCount(2);
        var dx = usage.FeatureBreakdown.Single(f => f.Feature == "Diagnosis");
        dx.Calls.Should().Be(2);
        dx.PromptTokens.Should().Be(300);
        dx.CompletionTokens.Should().Be(130);
        dx.CostUsd.Should().Be(0.0022m);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private SpaceAiSettingsService NewSvc() =>
        new(postgres,
            new FixedSpaceContext(WellKnown.DefaultSpaceId),
            NewEncryptionService(),
            NullLogger<SpaceAiSettingsService>.Instance);

    private static AesEncryptionService NewEncryptionService() =>
        new(Convert.ToBase64String(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));

    private static AiCallLog NewLog(
        DateTimeOffset when, string feature,
        int prompt, int completion, decimal cost) =>
        new()
        {
            SpaceId          = WellKnown.DefaultSpaceId,
            Provider         = "Anthropic",
            Model            = "claude-sonnet-4.6",
            Feature          = feature,
            PromptTokens     = prompt,
            CompletionTokens = completion,
            LatencyMs        = 250,
            CostUsd          = cost,
            Success          = true,
        };

    private sealed class FixedSpaceContext(Guid spaceId) : ISpaceContext
    {
        public Guid CurrentSpaceId => spaceId;
        public bool IsSystemAdmin  => false;
        public IDisposable WithSpace(Guid newSpaceId) => new NoOp();
        private sealed class NoOp : IDisposable { public void Dispose() { } }
    }
}
