using FluentAssertions;
using KrakenDeploy.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Ai.Tests;

/// <summary>
/// Tests for the <see cref="KrakenAi"/> wrapper's feature-flag enforcement.
/// We use a stub settings provider and a stub factory so no real provider
/// is contacted. The wrapper's contract is:
/// <list type="bullet">
///   <item>Throw <see cref="KrakenAiDisabledException"/> when provider is Disabled.</item>
///   <item>Throw <see cref="KrakenAiFeatureDisabledException"/> when the per-feature flag is off.</item>
///   <item>Pass through to the underlying <c>IChatClient</c> when fully configured.</item>
/// </list>
/// </summary>
public sealed class KrakenAiTests
{
    [Fact]
    public async Task CompleteAsync_throws_when_per_feature_flag_is_off()
    {
        // Provider configured, but Adhoc feature is off.
        var settings = new KrakenAiSettings
        {
            Provider = KrakenAiProvider.OpenAI,
            Model    = "gpt-4o-mini",
            ApiKey   = "sk-test-key-not-real",
            Features = new KrakenAiFeatureFlags { AdhocEnabled = false },
        };

        var ai = NewAi(settings, new ThrowingFactory());

        Func<Task> act = () => ai.CompleteAsync(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            feature: KrakenAiFeature.Adhoc);

        await act.Should().ThrowAsync<KrakenAiFeatureDisabledException>()
            .Where(e => e.Feature == nameof(KrakenAiFeature.Adhoc));
    }

    [Fact]
    public async Task CompleteAsync_uses_factory_when_feature_is_enabled()
    {
        var settings = new KrakenAiSettings
        {
            Provider = KrakenAiProvider.OpenAI,
            Model    = "gpt-4o-mini",
            ApiKey   = "sk-test-key-not-real",
            Features = new KrakenAiFeatureFlags { DiagnosisEnabled = true },
        };

        var factory = new RecordingFactory();
        var ai = NewAi(settings, factory);

        // The fake client returns an empty completion; we don't care about
        // its content — just that the factory was called once with the
        // correct settings.
        await ai.CompleteAsync(
            messages: [new ChatMessage(ChatRole.User, "anything")],
            feature: KrakenAiFeature.Diagnosis);

        factory.LastSettings.Should().NotBeNull();
        factory.LastSettings!.Provider.Should().Be(KrakenAiProvider.OpenAI);
        factory.LastSettings.Model.Should().Be("gpt-4o-mini");
    }

    [Fact]
    public async Task CompleteAsync_returns_provider_and_model_in_completion()
    {
        var settings = new KrakenAiSettings
        {
            Provider = KrakenAiProvider.Anthropic,
            Model    = "claude-3-5-sonnet-20241022",
            ApiKey   = "k",
            Features = new KrakenAiFeatureFlags { DiagnosisEnabled = true },
        };

        var ai = NewAi(settings, new RecordingFactory());

        var result = await ai.CompleteAsync(
            messages: [new ChatMessage(ChatRole.User, "x")],
            feature: KrakenAiFeature.Diagnosis);

        result.Provider.Should().Be(nameof(KrakenAiProvider.Anthropic));
        result.Model.Should().Be("claude-3-5-sonnet-20241022");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static KrakenAi NewAi(KrakenAiSettings settings, KrakenAiClientFactory factory) =>
        new(factory,
            new StubSettingsProvider(settings),
            new PromptSanitizer(),
            new NullKrakenAiCallSink(),
            NullLogger<KrakenAi>.Instance);

    private sealed class StubSettingsProvider(KrakenAiSettings settings) : IKrakenAiSettingsProvider
    {
        public ValueTask<KrakenAiSettings> GetAsync(CancellationToken ct = default)
            => ValueTask.FromResult(settings);
    }

    /// <summary>Throws if called — used in tests where the wrapper must
    /// short-circuit before reaching the factory.</summary>
    private sealed class ThrowingFactory : KrakenAiClientFactory
    {
        public override IChatClient CreateClient(KrakenAiSettings settings)
            => throw new InvalidOperationException(
                "Factory should not be invoked when feature is disabled.");
    }

    /// <summary>Records the settings passed in + returns a no-op client.</summary>
    private sealed class RecordingFactory : KrakenAiClientFactory
    {
        public KrakenAiSettings? LastSettings { get; private set; }

        public override IChatClient CreateClient(KrakenAiSettings settings)
        {
            LastSettings = settings;
            return new FakeChatClient();
        }
    }

    /// <summary>Minimal <see cref="IChatClient"/> impl returning empty responses.</summary>
    private sealed class FakeChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "")));

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
