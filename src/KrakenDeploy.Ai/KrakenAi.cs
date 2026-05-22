using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
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
    private readonly IPromptSanitizer _sanitizer;
    private readonly IKrakenAiCallSink _callSink;
    private readonly IAiCostCatalog _costCatalog;
    private readonly IBudgetTracker _budgetTracker;
    private readonly ILogger<KrakenAi> _logger;

    public KrakenAi(
        KrakenAiClientFactory factory,
        IKrakenAiSettingsProvider settingsProvider,
        IPromptSanitizer sanitizer,
        IKrakenAiCallSink callSink,
        IAiCostCatalog costCatalog,
        IBudgetTracker budgetTracker,
        ILogger<KrakenAi> logger)
    {
        _factory          = factory;
        _settingsProvider = settingsProvider;
        _sanitizer        = sanitizer;
        _callSink         = callSink;
        _costCatalog      = costCatalog;
        _budgetTracker    = budgetTracker;
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

        var (sanitisedMessages, scrubbedNames) = ApplySanitization(messages, options);

        var sw = Stopwatch.StartNew();
        ChatResponse? response = null;
        Exception?    error    = null;
        try
        {
            response = await client
                .GetResponseAsync(sanitisedMessages, ToChatOptions(options), ct)
                .ConfigureAwait(false);
            return BuildCompletion(settings, response, sw.Elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex;
            throw;
        }
        finally
        {
            sw.Stop();
            await EmitAuditAsync(
                settings, feature, options, sanitisedMessages, scrubbedNames,
                response, sw.Elapsed, error, ct).ConfigureAwait(false);
        }
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

        var (sanitisedMessages, scrubbedNames) = ApplySanitization(messages, options);

        // Microsoft.Extensions.AI.IChatClient.GetResponseAsync<T> wraps the
        // provider's structured-output mode (Anthropic tool use / OpenAI
        // response_format=json_schema) and deserialises to TResult. Single
        // call site for every structured-prompt across the codebase.
        var sw = Stopwatch.StartNew();
        ChatResponse<TResult>? response = null;
        Exception? error = null;
        try
        {
            response = await client
                .GetResponseAsync<TResult>(sanitisedMessages, ToChatOptions(options), cancellationToken: ct)
                .ConfigureAwait(false);
            if (response.TryGetResult(out var result))
            {
                return result;
            }
            error = new InvalidOperationException(
                $"AI provider returned a response that could not be parsed as {typeof(TResult).Name}. " +
                $"Raw text: {response.Text}");
            throw error;
        }
        catch (Exception ex) when (error is null && ex is not OperationCanceledException)
        {
            error = ex;
            throw;
        }
        finally
        {
            sw.Stop();
            await EmitAuditAsync(
                settings, feature, options, sanitisedMessages, scrubbedNames,
                response?.Messages.FirstOrDefault() is { } _
                    ? new ChatResponse(response.Messages) { Usage = response.Usage }
                    : null,
                sw.Elapsed, error, ct).ConfigureAwait(false);
        }
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

        var (sanitisedMessages, scrubbedNames) = ApplySanitization(messages, options);

        var sw            = Stopwatch.StartNew();
        var accumulator   = new StringBuilder();
        Exception? error  = null;

        IAsyncEnumerator<ChatResponseUpdate> enumerator;
        try
        {
            enumerator = client
                .GetStreamingResponseAsync(sanitisedMessages, ToChatOptions(options), ct)
                .GetAsyncEnumerator(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            await EmitAuditAsync(
                settings, feature, options, sanitisedMessages, scrubbedNames,
                response: null, sw.Elapsed, ex, ct).ConfigureAwait(false);
            throw;
        }

        await using (enumerator.ConfigureAwait(false))
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    error = ex;
                    break;
                }
                if (!hasNext) { break; }

                var text = enumerator.Current.Text;
                if (!string.IsNullOrEmpty(text))
                {
                    accumulator.Append(text);
                    yield return text;
                }
            }
        }

        sw.Stop();
        // For streaming responses Microsoft.Extensions.AI doesn't always
        // surface usage stats — emit a partial audit row capturing what we
        // know (latency + accumulated text length as a proxy on the body
        // when LogPromptBodies is on).
        await EmitStreamingAuditAsync(
            settings, feature, options, sanitisedMessages, scrubbedNames,
            accumulator.ToString(), sw.Elapsed, error, ct).ConfigureAwait(false);
        if (error is not null) { throw error; }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the current Space's settings + enforces three gates:
    /// global "AI is configured", per-feature flag, and the monthly
    /// budget cap (M11.A.5). Throws the appropriate
    /// <see cref="KrakenAiException"/> on failure.
    /// </summary>
    private async ValueTask<KrakenAiSettings> GuardAsync(
        KrakenAiFeature feature, CancellationToken ct)
    {
        var settings = await _settingsProvider.GetAsync(ct).ConfigureAwait(false);
        EnsureFeatureEnabled(settings, feature);
        await EnsureBudgetAsync(settings, ct).ConfigureAwait(false);
        return settings;
    }

    /// <summary>
    /// Budget gate: when the Space has a positive monthly cap and the
    /// current MTD spend has reached it, refuse the call before it
    /// reaches the provider. Zero / negative cap = no enforcement.
    /// </summary>
    private async ValueTask EnsureBudgetAsync(
        KrakenAiSettings settings, CancellationToken ct)
    {
        if (settings.BudgetUsdPerMonth <= 0m)
        {
            return; // No cap.
        }

        var mtd = await _budgetTracker.GetMonthToDateUsdAsync(ct).ConfigureAwait(false);
        if (mtd >= settings.BudgetUsdPerMonth)
        {
            throw new KrakenAiBudgetExceededException(mtd, settings.BudgetUsdPerMonth);
        }
    }

    /// <summary>
    /// Computes the USD cost of a single call from the rate catalog +
    /// reported token counts. Returns 0 when the catalog doesn't know
    /// the provider/model pair — logs a warning so operators see the gap.
    /// Computed in <c>numeric(12,6)</c>-friendly precision; we don't
    /// round here, the DB column handles fractional cent precision.
    /// </summary>
    private decimal ComputeCost(
        KrakenAiSettings settings, int promptTokens, int completionTokens)
    {
        var rate = _costCatalog.TryGetRate(settings.Provider, settings.Model ?? string.Empty);
        if (rate is null)
        {
            _logger.LogWarning(
                "AI cost catalog has no rate for {Provider}/{Model}; recording cost as $0. " +
                "Update IAiCostCatalog to track this model.",
                settings.Provider, settings.Model);
            return 0m;
        }
        // Tokens / 1000 × per-1k rate. decimal arithmetic to avoid float drift.
        return (promptTokens     / 1000m) * rate.InputUsdPer1k
             + (completionTokens / 1000m) * rate.OutputUsdPer1k;
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

    /// <summary>
    /// Runs the prompt through <see cref="IPromptSanitizer"/> when the
    /// caller supplied a <see cref="KrakenAiRequestOptions.SensitiveValues"/>
    /// map. Returns the original messages + empty scrubbed-name list when
    /// no map was supplied — the wrapper stays a no-op for callers that
    /// don't touch user-supplied variables (purely synthetic prompts).
    /// </summary>
    private (IReadOnlyList<ChatMessage> Messages, IReadOnlyList<string> ScrubbedNames)
        ApplySanitization(
            IReadOnlyList<ChatMessage> messages, KrakenAiRequestOptions? options)
    {
        if (options?.SensitiveValues is null || options.SensitiveValues.Count == 0)
        {
            return (messages, Array.Empty<string>());
        }
        var result = _sanitizer.Sanitize(messages, options.SensitiveValues);
        return (result.Messages, result.ScrubbedNames);
    }

    private static KrakenAiCompletion BuildCompletion(
        KrakenAiSettings settings, ChatResponse response, TimeSpan latency) =>
        new(
            Text:             response.Text ?? string.Empty,
            PromptTokens:     (int)(response.Usage?.InputTokenCount  ?? 0),
            CompletionTokens: (int)(response.Usage?.OutputTokenCount ?? 0),
            Latency:          latency,
            Provider:         settings.Provider.ToString(),
            Model:            settings.Model ?? "");

    /// <summary>
    /// Writes one audit row to the sink for a non-streaming call. Never
    /// throws — sink failures are logged and swallowed because audit
    /// emission must not break the user-facing AI surface.
    /// </summary>
    private async Task EmitAuditAsync(
        KrakenAiSettings settings,
        KrakenAiFeature  feature,
        KrakenAiRequestOptions? options,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<string> scrubbedNames,
        ChatResponse? response,
        TimeSpan latency,
        Exception? error,
        CancellationToken ct)
    {
        var prompt = (int)(response?.Usage?.InputTokenCount  ?? 0);
        var output = (int)(response?.Usage?.OutputTokenCount ?? 0);

        var entry = new AiCallLogEntry
        {
            Provider              = settings.Provider.ToString(),
            Model                 = settings.Model ?? string.Empty,
            Feature               = feature.ToString(),
            PromptTokens          = prompt,
            CompletionTokens      = output,
            LatencyMs             = (int)latency.TotalMilliseconds,
            CostUsd               = ComputeCost(settings, prompt, output),
            Success               = error is null,
            ErrorMessage          = SanitizeError(error),
            CorrelationId         = options?.CorrelationId,
            ScrubbedVariableNames = scrubbedNames.Count == 0
                ? null
                : string.Join(',', scrubbedNames),
            PromptBodyJson        = settings.LogPromptBodies ? SerializePrompt(messages) : null,
            ResponseBody          = settings.LogPromptBodies ? response?.Text            : null,
        };

        try
        {
            await _callSink.WriteAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception sinkError)
        {
            _logger.LogError(sinkError,
                "KrakenAi audit sink failed; AI call itself succeeded={Success}.",
                error is null);
        }
    }

    /// <summary>
    /// Variant for streaming completions — we don't have a final
    /// <c>ChatResponse</c>, just the accumulated text. Token counts are
    /// zero because most providers don't surface usage stats on the
    /// streaming wire.
    /// </summary>
    private async Task EmitStreamingAuditAsync(
        KrakenAiSettings settings,
        KrakenAiFeature  feature,
        KrakenAiRequestOptions? options,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<string> scrubbedNames,
        string accumulatedText,
        TimeSpan latency,
        Exception? error,
        CancellationToken ct)
    {
        var entry = new AiCallLogEntry
        {
            Provider              = settings.Provider.ToString(),
            Model                 = settings.Model ?? string.Empty,
            Feature               = feature.ToString(),
            // Streaming completions don't surface usage stats on the wire
            // for most providers — cost stays at $0 unless we ever switch
            // to a streaming path that supports usage reporting (Anthropic
            // beta in 2026; OpenAI o4-stream).
            PromptTokens          = 0,
            CompletionTokens      = 0,
            LatencyMs             = (int)latency.TotalMilliseconds,
            CostUsd               = 0m,
            Success               = error is null,
            ErrorMessage          = SanitizeError(error),
            CorrelationId         = options?.CorrelationId,
            ScrubbedVariableNames = scrubbedNames.Count == 0
                ? null
                : string.Join(',', scrubbedNames),
            PromptBodyJson        = settings.LogPromptBodies ? SerializePrompt(messages) : null,
            ResponseBody          = settings.LogPromptBodies ? accumulatedText           : null,
        };

        try
        {
            await _callSink.WriteAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception sinkError)
        {
            _logger.LogError(sinkError,
                "KrakenAi audit sink failed for streaming call; AI call itself succeeded={Success}.",
                error is null);
        }
    }

    /// <summary>
    /// Serialises the chat messages array to JSON for the audit row's
    /// PromptBodyJson column. Only invoked when LogPromptBodies is on.
    /// </summary>
    private static string SerializePrompt(IReadOnlyList<ChatMessage> messages)
    {
        var simplified = messages.Select(m => new
        {
            role    = m.Role.Value,
            content = m.Text,
        });
        return JsonSerializer.Serialize(simplified);
    }

    /// <summary>
    /// Cleans up the error message for audit storage. Strips any value
    /// resembling an API key (anything that looks like <c>sk-…</c> or a
    /// long base64-ish blob) so a botched call doesn't bleed credentials
    /// into the audit table.
    /// </summary>
    private static string? SanitizeError(Exception? error)
    {
        if (error is null) { return null; }
        var message = $"{error.GetType().Name}: {error.Message}";
        // Crude but effective: redact anything resembling an api key.
        message = System.Text.RegularExpressions.Regex.Replace(
            message,
            @"(sk-[A-Za-z0-9_\-]{16,}|[A-Za-z0-9_\-]{40,})",
            "<redacted>");
        return message.Length > 4096 ? message[..4096] : message;
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
