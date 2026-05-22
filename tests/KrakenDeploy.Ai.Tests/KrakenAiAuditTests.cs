using FluentAssertions;
using KrakenDeploy.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Ai.Tests;

/// <summary>
/// Tests for the audit-emission contract added in M11.A.3.
/// <list type="bullet">
///   <item>Every successful call emits one AiCallLogEntry to the sink.</item>
///   <item>Every failed call emits one too — the row carries the redacted error.</item>
///   <item>Streaming calls emit one entry on completion.</item>
///   <item>API keys never appear in the audit row's error message.</item>
///   <item>Prompt + response bodies appear only when <c>LogPromptBodies</c> is on.</item>
///   <item>Sink failures NEVER propagate out of <c>IKrakenAi</c> — best-effort audit.</item>
/// </list>
/// </summary>
public sealed class KrakenAiAuditTests
{
    [Fact]
    public async Task Successful_call_emits_one_audit_row_with_provider_and_feature()
    {
        var sink = new RecordingSink();
        var ai = NewAi(settings: HappySettings(), factory: new HappyFactory(), sink: sink);

        await ai.CompleteAsync(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            feature: KrakenAiFeature.Diagnosis);

        sink.Entries.Should().ContainSingle();
        var entry = sink.Entries[0];
        entry.Provider.Should().Be(nameof(KrakenAiProvider.Anthropic));
        entry.Feature.Should().Be(nameof(KrakenAiFeature.Diagnosis));
        entry.Success.Should().BeTrue();
        entry.ErrorMessage.Should().BeNull();
        entry.LatencyMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Failed_call_emits_one_audit_row_with_success_false_and_redacted_error()
    {
        var sink = new RecordingSink();
        var factory = new ThrowingChatClientFactory(
            new InvalidOperationException("oops: api key sk-abcdef1234567890ABCDEFGHIJ failed"));
        var ai = NewAi(HappySettings(), factory, sink);

        Func<Task> act = () => ai.CompleteAsync(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            feature: KrakenAiFeature.Diagnosis);

        await act.Should().ThrowAsync<InvalidOperationException>();

        sink.Entries.Should().ContainSingle();
        var entry = sink.Entries[0];
        entry.Success.Should().BeFalse();
        entry.ErrorMessage.Should().NotBeNull();
        entry.ErrorMessage.Should().Contain("InvalidOperationException");
        // Redaction: anything looking like an API key gets <redacted>
        entry.ErrorMessage.Should().NotContain("sk-abcdef1234567890ABCDEFGHIJ",
            "API-key shapes must never appear in audit rows");
        entry.ErrorMessage.Should().Contain("<redacted>");
    }

    [Fact]
    public async Task Body_columns_are_null_when_LogPromptBodies_is_off()
    {
        var sink = new RecordingSink();
        var ai = NewAi(
            settings: HappySettings() with { LogPromptBodies = false },
            factory: new HappyFactory(),
            sink: sink);

        await ai.CompleteAsync(
            messages: [new ChatMessage(ChatRole.User, "do not log me")],
            feature: KrakenAiFeature.Diagnosis);

        var entry = sink.Entries[0];
        entry.PromptBodyJson.Should().BeNull();
        entry.ResponseBody.Should().BeNull();
    }

    [Fact]
    public async Task Body_columns_are_populated_when_LogPromptBodies_is_on()
    {
        var sink = new RecordingSink();
        var ai = NewAi(
            settings: HappySettings() with { LogPromptBodies = true },
            factory: new HappyFactory(responseText: "the reply"),
            sink: sink);

        await ai.CompleteAsync(
            messages: [new ChatMessage(ChatRole.User, "the question")],
            feature: KrakenAiFeature.Diagnosis);

        var entry = sink.Entries[0];
        entry.PromptBodyJson.Should().NotBeNull();
        entry.PromptBodyJson.Should().Contain("the question");
        entry.ResponseBody.Should().Be("the reply");
    }

    [Fact]
    public async Task CorrelationId_propagates_into_the_audit_row()
    {
        var sink = new RecordingSink();
        var ai = NewAi(HappySettings(), new HappyFactory(), sink);

        await ai.CompleteAsync(
            messages: [new ChatMessage(ChatRole.User, "x")],
            feature: KrakenAiFeature.Adhoc,
            options:  new KrakenAiRequestOptions { CorrelationId = "session-42-iter-1" });

        sink.Entries[0].CorrelationId.Should().Be("session-42-iter-1");
    }

    [Fact]
    public async Task Sink_failure_does_not_propagate_out_of_IKrakenAi()
    {
        // Best-effort audit: a sink throwing must NEVER break the user-facing call.
        var sink = new ThrowingSink();
        var ai = NewAi(HappySettings(), new HappyFactory(), sink);

        var result = await ai.CompleteAsync(
            messages: [new ChatMessage(ChatRole.User, "still works")],
            feature: KrakenAiFeature.Diagnosis);

        // No exception bubbled — the AI call returned successfully.
        result.Should().NotBeNull();
        sink.AttemptedWrites.Should().Be(1,
            "the wrapper still tried to write, the sink just swallowed the success");
    }

    [Fact]
    public async Task Feature_disabled_does_NOT_emit_audit_row()
    {
        // When a feature is disabled, we short-circuit before contacting the
        // provider. No audit row is written — there's nothing to attribute.
        var sink = new RecordingSink();
        var ai = NewAi(
            settings: HappySettings() with
            {
                Features = new KrakenAiFeatureFlags { DiagnosisEnabled = false },
            },
            factory: new HappyFactory(),
            sink:    sink);

        Func<Task> act = () => ai.CompleteAsync(
            messages: [new ChatMessage(ChatRole.User, "hi")],
            feature: KrakenAiFeature.Diagnosis);

        await act.Should().ThrowAsync<KrakenAiFeatureDisabledException>();
        sink.Entries.Should().BeEmpty(
            "feature-disabled is a configuration error before any LLM contact, " +
            "so we don't attribute it as a 'call'");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static KrakenAiSettings HappySettings() => new()
    {
        Provider = KrakenAiProvider.Anthropic,
        Model    = "claude-3-5-sonnet-20241022",
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
        KrakenAiClientFactory factory,
        IKrakenAiCallSink sink)
        => new(factory,
               new StubSettingsProvider(settings),
               sink,
               NullLogger<KrakenAi>.Instance);

    private sealed class StubSettingsProvider(KrakenAiSettings settings) : IKrakenAiSettingsProvider
    {
        public ValueTask<KrakenAiSettings> GetAsync(CancellationToken ct = default)
            => ValueTask.FromResult(settings);
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

    private sealed class ThrowingSink : IKrakenAiCallSink
    {
        public int AttemptedWrites { get; private set; }
        public ValueTask WriteAsync(AiCallLogEntry entry, CancellationToken ct = default)
        {
            AttemptedWrites++;
            throw new InvalidOperationException("audit DB down");
        }
    }

    /// <summary>Factory whose IChatClient returns a fixed response.</summary>
    private sealed class HappyFactory(string responseText = "") : KrakenAiClientFactory
    {
        public override IChatClient CreateClient(KrakenAiSettings settings)
            => new HappyChatClient(responseText);
    }

    /// <summary>Factory whose IChatClient throws on every call.</summary>
    private sealed class ThrowingChatClientFactory(Exception error) : KrakenAiClientFactory
    {
        public override IChatClient CreateClient(KrakenAiSettings settings)
            => new ThrowingChatClient(error);
    }

    private sealed class HappyChatClient(string text) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));

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

    private sealed class ThrowingChatClient(Exception error) : IChatClient
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
