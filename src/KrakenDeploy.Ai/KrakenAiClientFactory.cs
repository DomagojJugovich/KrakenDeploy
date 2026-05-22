using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;

namespace KrakenDeploy.Ai;

/// <summary>
/// Builds an <c>Microsoft.Extensions.AI.IChatClient</c> from a Space's
/// <see cref="KrakenAiSettings"/>. The factory is the single point of
/// provider-specific wiring — every other piece of <c>KrakenDeploy.Ai</c>
/// (the wrapper, the audit, the budget check) operates on the unified
/// <c>IChatClient</c> abstraction.
/// <para>
/// Provider dispatch:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="KrakenAiProvider.Anthropic"/> → <c>Anthropic.AnthropicClient.AsIChatClient(model)</c>.</description></item>
///   <item><description><see cref="KrakenAiProvider.OpenAI"/> → <c>OpenAI.OpenAIClient(key).GetChatClient(model).AsIChatClient()</c>.</description></item>
///   <item><description><see cref="KrakenAiProvider.AzureOpenAI"/> → same as OpenAI but with a custom <c>endpoint</c> override.</description></item>
///   <item><description><see cref="KrakenAiProvider.DeepSeek"/> → OpenAI client pointed at <c>https://api.deepseek.com/v1</c>.</description></item>
///   <item><description><see cref="KrakenAiProvider.LocalOpenAiCompatible"/> → OpenAI client pointed at the operator-supplied base URL (Ollama / LM Studio / vLLM).</description></item>
///   <item><description><see cref="KrakenAiProvider.Disabled"/> → throws <see cref="KrakenAiDisabledException"/>.</description></item>
/// </list>
/// </summary>
public class KrakenAiClientFactory
{
    /// <summary>
    /// Constructs an <see cref="IChatClient"/> for the given Space settings.
    /// Caller owns the returned instance's lifetime; the factory does NOT
    /// cache. Pluggable providers expose different connection pools, and a
    /// stale cached client across a settings change would hand back a
    /// client bound to the old API key.
    /// </summary>
    public virtual IChatClient CreateClient(KrakenAiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Provider == KrakenAiProvider.Disabled)
        {
            throw new KrakenAiDisabledException("provider is set to Disabled.");
        }
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new KrakenAiDisabledException("API key is not set.");
        }
        if (string.IsNullOrWhiteSpace(settings.Model))
        {
            throw new KrakenAiDisabledException("model is not set.");
        }

        return settings.Provider switch
        {
            KrakenAiProvider.Anthropic             => CreateAnthropicClient(settings),
            KrakenAiProvider.OpenAI                => CreateOpenAiClient(settings, defaultEndpoint: null),
            KrakenAiProvider.AzureOpenAI           => CreateOpenAiClient(settings, RequiredBaseUrl(settings)),
            KrakenAiProvider.DeepSeek              => CreateOpenAiClient(settings, new Uri("https://api.deepseek.com/v1")),
            KrakenAiProvider.LocalOpenAiCompatible => CreateOpenAiClient(settings, RequiredBaseUrl(settings)),
            _ => throw new InvalidOperationException(
                $"Unknown KrakenAiProvider: {settings.Provider}."),
        };
    }

    /// <summary>
    /// Display string for the provider+model pair — surfaced in the
    /// <c>AiCallLog</c> row so audit consumers don't have to decode an
    /// integer enum.
    /// </summary>
    public static string DisplayName(KrakenAiSettings settings) =>
        $"{settings.Provider}/{settings.Model}";

    // ── Anthropic ─────────────────────────────────────────────────────────

    private static IChatClient CreateAnthropicClient(KrakenAiSettings settings)
    {
        // The official Anthropic NuGet (12.22+) takes API key + (optional)
        // base URL via property initialisers; AsIChatClient(model) is the
        // extension that adapts the client to Microsoft.Extensions.AI.
        // Honour an operator-set BaseUrl for self-hosted Anthropic proxies
        // (e.g. an enterprise gateway); falls back to api.anthropic.com.
        var anthropic = new global::Anthropic.AnthropicClient
        {
            ApiKey  = settings.ApiKey!,
            BaseUrl = string.IsNullOrWhiteSpace(settings.BaseUrl)
                ? null!
                : settings.BaseUrl,
        };
        return anthropic.AsIChatClient(settings.Model!);
    }

    // ── OpenAI / Azure OpenAI / DeepSeek / Local ─────────────────────────

    private static IChatClient CreateOpenAiClient(KrakenAiSettings settings, Uri? defaultEndpoint)
    {
        // OpenAI's SDK plus Microsoft.Extensions.AI.OpenAI's adapter cover
        // every OpenAI-compatible endpoint: the official API, Azure, DeepSeek,
        // self-hosted Ollama / LM Studio / vLLM. Only the endpoint and the
        // API key change.
        var options = defaultEndpoint is null
            ? new OpenAIClientOptions()
            : new OpenAIClientOptions { Endpoint = defaultEndpoint };

        var openAi  = new OpenAIClient(new ApiKeyCredential(settings.ApiKey!), options);
        return openAi.GetChatClient(settings.Model!).AsIChatClient();
    }

    private static Uri RequiredBaseUrl(KrakenAiSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            throw new KrakenAiDisabledException(
                $"provider {settings.Provider} requires BaseUrl to be set.");
        }
        if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var uri))
        {
            throw new KrakenAiDisabledException(
                $"BaseUrl '{settings.BaseUrl}' is not a valid absolute URI.");
        }
        return uri;
    }
}
