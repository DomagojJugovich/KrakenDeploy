using FluentAssertions;
using KrakenDeploy.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Ai.Tests;

/// <summary>
/// Tests for the M11.A.5 budget gate + cost-population contract.
/// </summary>
public sealed class KrakenAiBudgetTests
{
    [Fact]
    public async Task Budget_cap_zero_means_no_enforcement_even_at_high_MTD()
    {
        // Cap = 0 → no limit. The wrapper proceeds with the call regardless
        // of what the tracker reports.
        var ai = NewAi(
            settings: HappySettings() with { BudgetUsdPerMonth = 0m },
            budget:   new FixedBudgetTracker(currentMtd: 9_999.99m));

        var result = await ai.CompleteAsync(
            messages: [new ChatMessage(ChatRole.User, "x")],
            feature: KrakenAiFeature.Diagnosis);

        result.Should().NotBeNull(
            "BudgetUsdPerMonth = 0 means 'no cap' — the call must go through " +
            "no matter what the tracker reports");
    }

    [Fact]
    public async Task Budget_cap_negative_means_no_enforcement()
    {
        var ai = NewAi(
            settings: HappySettings() with { BudgetUsdPerMonth = -1m },
            budget:   new FixedBudgetTracker(currentMtd: 9_999.99m));

        var result = await ai.CompleteAsync(
            messages: [new ChatMessage(ChatRole.User, "x")],
            feature: KrakenAiFeature.Diagnosis);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task Call_under_budget_proceeds()
    {
        var ai = NewAi(
            settings: HappySettings() with { BudgetUsdPerMonth = 100m },
            budget:   new FixedBudgetTracker(currentMtd: 42.50m));

        var result = await ai.CompleteAsync(
            messages: [new ChatMessage(ChatRole.User, "x")],
            feature: KrakenAiFeature.Diagnosis);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task Call_at_or_over_budget_throws_KrakenAiBudgetExceededException()
    {
        var ai = NewAi(
            settings: HappySettings() with { BudgetUsdPerMonth = 100m },
            budget:   new FixedBudgetTracker(currentMtd: 100.00m));

        Func<Task> act = () => ai.CompleteAsync(
            messages: [new ChatMessage(ChatRole.User, "x")],
            feature: KrakenAiFeature.Diagnosis);

        var ex = await act.Should().ThrowAsync<KrakenAiBudgetExceededException>();
        ex.Which.MonthToDateUsd.Should().Be(100m);
        ex.Which.CapUsd.Should().Be(100m);
    }

    [Fact]
    public async Task Budget_check_runs_BEFORE_the_provider_is_called()
    {
        // The whole point of the pre-check: when MTD ≥ cap, no provider
        // call goes out, period. We use a factory that throws if invoked
        // — if the budget gate let the call through, this test would
        // surface a different exception.
        var factory = new ThrowIfInvokedFactory();
        var ai = NewAi(
            settings: HappySettings() with { BudgetUsdPerMonth = 50m },
            budget:   new FixedBudgetTracker(currentMtd: 99.99m),
            factory:  factory);

        Func<Task> act = () => ai.CompleteAsync(
            messages: [new ChatMessage(ChatRole.User, "x")],
            feature: KrakenAiFeature.Diagnosis);

        await act.Should().ThrowAsync<KrakenAiBudgetExceededException>();
        factory.WasInvoked.Should().BeFalse(
            "the wrapper must short-circuit before reaching the provider");
    }

    [Fact]
    public async Task Cost_is_computed_from_token_counts_and_populated_on_audit_row()
    {
        var sink = new RecordingSink();
        // Anthropic Sonnet 4.6: $3/M input, $15/M output → 0.003 / 0.015 per 1k.
        var ai = NewAi(
            settings: HappySettings() with
            {
                Provider = KrakenAiProvider.Anthropic,
                Model    = "claude-sonnet-4.6",
            },
            sink:     sink,
            factory:  new TokenedHappyFactory(promptTokens: 1500, completionTokens: 500));

        await ai.CompleteAsync(
            messages: [new ChatMessage(ChatRole.User, "x")],
            feature: KrakenAiFeature.Diagnosis);

        var entry = sink.Entries.Should().ContainSingle().Subject;
        entry.PromptTokens.Should().Be(1500);
        entry.CompletionTokens.Should().Be(500);
        // 1500/1000 × 0.003 + 500/1000 × 0.015 = 0.0045 + 0.0075 = 0.0120
        entry.CostUsd.Should().Be(0.012m);
    }

    [Fact]
    public async Task Cost_is_zero_when_catalog_does_not_know_the_model()
    {
        var sink = new RecordingSink();
        var ai = NewAi(
            settings: HappySettings() with
            {
                Provider = KrakenAiProvider.Anthropic,
                Model    = "claude-future-not-in-catalog",
            },
            sink:     sink,
            factory:  new TokenedHappyFactory(promptTokens: 100, completionTokens: 50));

        await ai.CompleteAsync(
            messages: [new ChatMessage(ChatRole.User, "x")],
            feature: KrakenAiFeature.Diagnosis);

        sink.Entries[0].CostUsd.Should().Be(0m,
            "unknown model → zero cost, the wrapper logs a warning so " +
            "operators see the catalog gap");
    }

    [Fact]
    public async Task Failed_call_still_emits_audit_row_with_zero_cost()
    {
        // No usage stats on failure → no cost computation. The audit row
        // captures the failure but doesn't bill the Space for it.
        var sink = new RecordingSink();
        var ai = NewAi(
            settings: HappySettings() with
            {
                Provider = KrakenAiProvider.Anthropic,
                Model    = "claude-sonnet-4.6",
            },
            sink:    sink,
            factory: new AlwaysFailFactory(new InvalidOperationException("boom")));

        Func<Task> act = () => ai.CompleteAsync(
            messages: [new ChatMessage(ChatRole.User, "x")],
            feature: KrakenAiFeature.Diagnosis);

        await act.Should().ThrowAsync<InvalidOperationException>();
        sink.Entries.Should().ContainSingle();
        sink.Entries[0].Success.Should().BeFalse();
        sink.Entries[0].CostUsd.Should().Be(0m);
    }

    [Fact]
    public async Task BudgetExceeded_does_NOT_emit_an_audit_row()
    {
        // Budget pre-check short-circuits before any work happens. No row
        // because no LLM contact was made — symmetric with the feature-disabled
        // contract from M11.A.3.
        var sink = new RecordingSink();
        var ai = NewAi(
            settings: HappySettings() with { BudgetUsdPerMonth = 100m },
            sink:     sink,
            budget:   new FixedBudgetTracker(currentMtd: 200m));

        Func<Task> act = () => ai.CompleteAsync(
            messages: [new ChatMessage(ChatRole.User, "x")],
            feature: KrakenAiFeature.Diagnosis);

        await act.Should().ThrowAsync<KrakenAiBudgetExceededException>();
        sink.Entries.Should().BeEmpty();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static KrakenAiSettings HappySettings() => new()
    {
        Provider = KrakenAiProvider.Anthropic,
        Model    = "claude-sonnet-4.6",
        ApiKey   = "k",
        Features = new KrakenAiFeatureFlags
        {
            DiagnosisEnabled = true,
            AdhocEnabled     = true,
            AssistantEnabled = true,
            McpEnabled       = true,
        },
    };

    private static KrakenAi NewAi(
        KrakenAiSettings settings,
        IBudgetTracker?  budget  = null,
        IKrakenAiCallSink? sink  = null,
        KrakenAiClientFactory? factory = null)
        => new(factory ?? new TokenedHappyFactory(0, 0),
               new StubSettingsProvider(settings),
               new PromptSanitizer(),
               sink   ?? new NullKrakenAiCallSink(),
               new AiCostCatalog(),
               budget ?? new NullBudgetTracker(),
               NullLogger<KrakenAi>.Instance);

    private sealed class StubSettingsProvider(KrakenAiSettings s) : IKrakenAiSettingsProvider
    {
        public ValueTask<KrakenAiSettings> GetAsync(CancellationToken ct = default)
            => ValueTask.FromResult(s);
    }

    private sealed class FixedBudgetTracker(decimal currentMtd) : IBudgetTracker
    {
        public ValueTask<decimal> GetMonthToDateUsdAsync(CancellationToken ct = default)
            => ValueTask.FromResult(currentMtd);
    }

    private sealed class RecordingSink : IKrakenAiCallSink
    {
        public List<AiCallLogEntry> Entries { get; } = [];
        public ValueTask WriteAsync(AiCallLogEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Factory that throws if its CreateClient is invoked at all.
    /// Used by tests that prove pre-checks short-circuit before any provider contact.</summary>
    private sealed class ThrowIfInvokedFactory : KrakenAiClientFactory
    {
        public bool WasInvoked { get; private set; }
        public override IChatClient CreateClient(KrakenAiSettings settings)
        {
            WasInvoked = true;
            throw new InvalidOperationException("Factory should not be invoked.");
        }
    }

    /// <summary>Factory whose IChatClient reports specific input + output token counts.</summary>
    private sealed class TokenedHappyFactory(int promptTokens, int completionTokens)
        : KrakenAiClientFactory
    {
        public override IChatClient CreateClient(KrakenAiSettings settings)
            => new TokenedChatClient(promptTokens, completionTokens);

        private sealed class TokenedChatClient(int prompt, int completion) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, ""))
                {
                    Usage = new UsageDetails
                    {
                        InputTokenCount  = prompt,
                        OutputTokenCount = completion,
                    },
                };
                return Task.FromResult(response);
            }

            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
                => EmptyAsync();

            private static async IAsyncEnumerable<ChatResponseUpdate> EmptyAsync()
            {
                await Task.CompletedTask;
                yield break;
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => null;
            public void Dispose() { }
        }
    }

    private sealed class AlwaysFailFactory(Exception error) : KrakenAiClientFactory
    {
        public override IChatClient CreateClient(KrakenAiSettings settings)
            => new AlwaysFailClient(error);

        private sealed class AlwaysFailClient(Exception error) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
                => Task.FromException<ChatResponse>(error);

            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
                => throw error;

            public object? GetService(Type serviceType, object? serviceKey = null) => null;
            public void Dispose() { }
        }
    }
}
