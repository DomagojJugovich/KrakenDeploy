using FluentAssertions;
using KrakenDeploy.Ai;

namespace KrakenDeploy.Ai.Tests;

/// <summary>
/// Provider-dispatch + configuration-validation tests for
/// <see cref="KrakenAiClientFactory"/>. We don't make real network calls
/// here — the factory only constructs an <c>IChatClient</c>, so verifying
/// the dispatch rules + the "fail loudly on misconfig" contract is what
/// matters at this layer.
/// </summary>
public sealed class KrakenAiClientFactoryTests
{
    [Fact]
    public void Disabled_provider_throws_KrakenAiDisabledException()
    {
        var factory = new KrakenAiClientFactory();
        var settings = new KrakenAiSettings { Provider = KrakenAiProvider.Disabled };

        Action act = () => factory.CreateClient(settings);

        act.Should().Throw<KrakenAiDisabledException>()
            .WithMessage("*Disabled*");
    }

    [Fact]
    public void Empty_API_key_throws_disabled_even_when_provider_is_set()
    {
        var factory = new KrakenAiClientFactory();
        var settings = new KrakenAiSettings
        {
            Provider = KrakenAiProvider.Anthropic,
            Model    = "claude-3-5-sonnet-20241022",
            ApiKey   = null,
        };

        Action act = () => factory.CreateClient(settings);

        act.Should().Throw<KrakenAiDisabledException>()
            .WithMessage("*API key*");
    }

    [Fact]
    public void Empty_model_throws_disabled()
    {
        var factory = new KrakenAiClientFactory();
        var settings = new KrakenAiSettings
        {
            Provider = KrakenAiProvider.Anthropic,
            ApiKey   = "anthropic-test-key",
            Model    = null,
        };

        Action act = () => factory.CreateClient(settings);

        act.Should().Throw<KrakenAiDisabledException>()
            .WithMessage("*model*");
    }

    [Theory]
    [InlineData(KrakenAiProvider.AzureOpenAI)]
    [InlineData(KrakenAiProvider.LocalOpenAiCompatible)]
    public void Provider_requiring_BaseUrl_throws_when_missing(KrakenAiProvider provider)
    {
        var factory = new KrakenAiClientFactory();
        var settings = new KrakenAiSettings
        {
            Provider = provider,
            Model    = "test-model",
            ApiKey   = "test-key",
            BaseUrl  = null,
        };

        Action act = () => factory.CreateClient(settings);

        act.Should().Throw<KrakenAiDisabledException>()
            .WithMessage("*BaseUrl*");
    }

    [Fact]
    public void Provider_requiring_BaseUrl_throws_on_malformed_url()
    {
        var factory = new KrakenAiClientFactory();
        var settings = new KrakenAiSettings
        {
            Provider = KrakenAiProvider.LocalOpenAiCompatible,
            Model    = "llama3.2:3b",
            ApiKey   = "ollama",
            BaseUrl  = "not-a-url",
        };

        Action act = () => factory.CreateClient(settings);

        act.Should().Throw<KrakenAiDisabledException>()
            .WithMessage("*absolute*");
    }

    [Fact]
    public void Constructs_a_client_for_OpenAI_with_only_key_and_model()
    {
        // Sanity: with provider=OpenAI and a key+model, we get back a client
        // without exception. We don't call into it — just prove the dispatch
        // assembled the right adapter.
        var factory = new KrakenAiClientFactory();
        var settings = new KrakenAiSettings
        {
            Provider = KrakenAiProvider.OpenAI,
            Model    = "gpt-4o-mini",
            ApiKey   = "sk-test-key-not-real",
        };

        using var client = factory.CreateClient(settings);
        client.Should().NotBeNull();
    }

    [Fact]
    public void Constructs_a_client_for_DeepSeek_without_explicit_BaseUrl()
    {
        // DeepSeek's base URL is hardcoded in the factory (api.deepseek.com/v1),
        // so callers don't need to supply BaseUrl. Validates that path.
        var factory = new KrakenAiClientFactory();
        var settings = new KrakenAiSettings
        {
            Provider = KrakenAiProvider.DeepSeek,
            Model    = "deepseek-chat",
            ApiKey   = "deepseek-test-key",
        };

        using var client = factory.CreateClient(settings);
        client.Should().NotBeNull();
    }

    [Fact]
    public void Constructs_a_client_for_Anthropic()
    {
        var factory = new KrakenAiClientFactory();
        var settings = new KrakenAiSettings
        {
            Provider = KrakenAiProvider.Anthropic,
            Model    = "claude-3-5-sonnet-20241022",
            ApiKey   = "anthropic-test-key",
        };

        using var client = factory.CreateClient(settings);
        client.Should().NotBeNull();
    }

    [Fact]
    public void DisplayName_renders_provider_and_model()
    {
        var settings = new KrakenAiSettings
        {
            Provider = KrakenAiProvider.Anthropic,
            Model    = "claude-3-5-sonnet-20241022",
            ApiKey   = "k",
        };
        KrakenAiClientFactory.DisplayName(settings)
            .Should().Be("Anthropic/claude-3-5-sonnet-20241022");
    }
}
