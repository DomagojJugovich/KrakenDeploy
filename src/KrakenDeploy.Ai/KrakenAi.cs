using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Ai;

/// <summary>
/// Default <see cref="IKrakenAi"/> implementation (M11.A.1 / A.2). Wraps a
/// provider's <c>IChatClient</c> with feature-flag enforcement, latency +
/// token telemetry, and structured-output deserialisation.
/// <para>
/// What's wired in this chunk (M11.A.1 + M11.A.2):
/// </para>
/// <list type="bullet">
///   <item><description>Provider dispatch via <see cref="KrakenAiClientFactory"/>.</description></item>
///   <item><description>Per-feature flag gating — <see cref="KrakenAiFeatureDisabledException"/> when a feature is off.</description></item>
///   <item><description>Latency + token telemetry surfaced on <see cref="KrakenAiCompletion"/>.</description></item>
///   <item><description>Structured-output JSON deserialisation.</description></item>
/// </list>
/// <para>
/// What lands in subsequent M11.A chunks (NOT in this commit):
/// </para>
/// <list type="bullet">
///   <item><description><see cref="IPromptSanitizer"/> stripping <c>Sensitive</c>-flagged variable values (M11.A.4).</description></item>
///   <item><description><c>AiCallLog</c> audit-row creation per call (M11.A.3).</description></item>
///   <item><description><see cref="KrakenAiBudgetExceededException"/> when month-to-date cost exceeds the cap (M11.A.5).</description></item>
/// </list>
/// </summary>
public sealed class KrakenAi : IKrakenAi
{
    private readonly KrakenAiClientFactory _factory;
    private readonly IKrakenAiSettingsProvider _settingsProvider;
    private readonly ILogger<KrakenAi> _logger;

    public KrakenAi(
        KrakenAiClientFactory factory,
        IKrakenAiSettingsProvider settingsProvider,
        ILogger<KrakenAi> logger)
    {
        _factory          = factory;
        _settingsProvider = settingsProvider;
        _logger           = logger;
    }

    public async Task<KrakenAiCompletion> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        KrakenAiFeature            feature,
        KrakenAiRequestOptions?    options = null,
        CancellationToken          ct      = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var settings = await GuardAsync(feature, ct).ConfigureAwait(false);
        using var client = _factory.CreateClient(settings);

        var sw       = Stopwatch.StartNew();
        var response = await client
            .GetResponseAsync(messages, ToChatOptions(options), ct)
            .ConfigureAwait(false);
        sw.Stop();

        var prompt   = response.Usage?.InputTokenCount  ?? 0;
        var output   = response.Usage?.OutputTokenCount ?? 0;

        return new KrakenAiCompletion(
            Text:             response.Text ?? string.Empty,
            PromptTokens:     (int)prompt,
            CompletionTokens: (int)output,
            Latency:          sw.Elapsed,
            Provider:         settings.Provider.ToString(),
            Model:            settings.Model ?? "");
    }

    public async Task<TResult> CompleteAsync<TResult>(
        IReadOnlyList<ChatMessage> messages,
        KrakenAiFeature            feature,
        KrakenAiRequestOptions?    options = null,
        CancellationToken          ct      = default)
        where TResult : class
    {
        ArgumentNullException.ThrowIfNull(messages);

        var settings = await GuardAsync(feature, ct).ConfigureAwait(false);
        using var client = _factory.CreateClient(settings);

        // Microsoft.Extensions.AI.IChatClient.GetResponseAsync<T> wraps the
        // provider's structured-output mode (Anthropic tool use / OpenAI
        // response_format=json_schema) and deserialises to TResult. Single
        // call site for every structured-prompt across the codebase.
        var response = await client
            .GetResponseAsync<TResult>(messages, ToChatOptions(options), cancellationToken: ct)
            .ConfigureAwait(false);

        if (response.TryGetResult(out var result))
        {
            return result;
        }
        throw new InvalidOperationException(
            $"AI provider returned a response that could not be parsed as {typeof(TResult).Name}. " +
            $"Raw text: {response.Text}");
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        IReadOnlyList<ChatMessage> messages,
        KrakenAiFeature            feature,
        KrakenAiRequestOptions?    options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var settings = await GuardAsync(feature, ct).ConfigureAwait(false);
        using var client = _factory.CreateClient(settings);

        await foreach (var update in client
            .GetStreamingResponseAsync(messages, ToChatOptions(options), ct)
            .ConfigureAwait(false))
        {
            // Each update may carry multiple content parts; concatenate any
            // text parts and yield the merged delta. Provider-specific
            // tool-call deltas are ignored — streaming surface is text-only
            // by design (the assistant UI doesn't need tools).
            var text = update.Text;
            if (!string.IsNullOrEmpty(text))
            {
                yield return text;
            }
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the current Space's settings + enforces both the global
    /// "AI is configured" gate and the per-feature flag. Throws the
    /// appropriate <see cref="KrakenAiException"/> on failure.
    /// </summary>
    private async ValueTask<KrakenAiSettings> GuardAsync(
        KrakenAiFeature feature, CancellationToken ct)
    {
        var settings = await _settingsProvider.GetAsync(ct).ConfigureAwait(false);
        EnsureFeatureEnabled(settings, feature);
        return settings;
    }

    private static void EnsureFeatureEnabled(KrakenAiSettings settings, KrakenAiFeature feature)
    {
        var enabled = feature switch
        {
            KrakenAiFeature.Diagnosis => settings.Features.DiagnosisEnabled,
            KrakenAiFeature.Adhoc     => settings.Features.AdhocEnabled,
            KrakenAiFeature.Assistant => settings.Features.AssistantEnabled,
            KrakenAiFeature.Mcp       => settings.Features.McpEnabled,
            _ => throw new ArgumentOutOfRangeException(nameof(feature), feature, null),
        };
        if (!enabled)
        {
            throw new KrakenAiFeatureDisabledException(feature.ToString());
        }
    }

    private static ChatOptions ToChatOptions(KrakenAiRequestOptions? options)
    {
        var chatOptions = new ChatOptions();
        if (options is null) { return chatOptions; }

        chatOptions.Temperature     = options.Temperature;
        chatOptions.MaxOutputTokens = options.MaxOutputTokens;
        return chatOptions;
    }
}

/// <summary>
/// Source of the current Space's <see cref="KrakenAiSettings"/> (M11.A.1).
/// Implementations live outside this project — the typical impl reads
/// from the Space settings row in the DB. Kept as an interface so unit
/// tests can stub it without dragging in EF Core.
/// </summary>
public interface IKrakenAiSettingsProvider
{
    /// <summary>
    /// Returns the current Space's settings, or a Disabled-defaulted
    /// instance when no Space context is set (e.g. background jobs that
    /// haven't been wired to a Space yet).
    /// </summary>
    ValueTask<KrakenAiSettings> GetAsync(CancellationToken ct = default);
}
