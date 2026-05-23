using FluentAssertions;
using KrakenDeploy.Ai;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Subscriptions;
using KrakenDeploy.Server.Data.Services.Subscriptions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Unit tests for <see cref="AiInspectTransport"/>. Stubs <c>IKrakenAi</c>
/// so we can assert the prompt content + verify the diagnosis lands as a
/// new audit event without standing up the full M11.A AI pipeline.
/// </summary>
public sealed class AiInspectTransportTests
{
    [Fact]
    public async Task DeliverAsync_writes_DiagnosisCompleted_audit_on_success()
    {
        // Pin the closure-loop contract: an AI inspection produces a new
        // audit event that's itself subscribable. Without this, the
        // "diagnose → post to Slack" chained-subscription workflow can't
        // be built — there's no event the second subscription can listen for.
        var ai = new StubAi("The deployment failed because the agent was offline.");
        var auditLog = new RecordingAuditLog();
        var transport = new AiInspectTransport(
            ai, auditLog, NullLogger<AiInspectTransport>.Instance);

        var result = await transport.DeliverAsync(
            DefaultSub(), NewEvent("Deployment.Failed"), default);

        result.Succeeded.Should().BeTrue();
        result.Detail.Should().Contain("tokens",
            "the success blurb shows the LLM cost the operator just incurred");

        auditLog.Recorded.Should().ContainSingle()
            .Which.EventType.Should().Be(AuditEventType.DiagnosisCompleted,
                "the AI's response becomes its own audit event so a " +
                "second subscription can route the diagnosis somewhere");

        var diagnosis = auditLog.Recorded.Single();
        diagnosis.SubjectType.Should().Be("AuditEntry",
            "the diagnosis subject is the original event so an operator " +
            "can pivot from audit row to its diagnosis");
        diagnosis.Details.Should().Contain("agent was offline");
    }

    [Fact]
    public async Task DeliverAsync_substitutes_event_fields_into_default_prompt()
    {
        var ai = new StubAi("ok");
        var transport = new AiInspectTransport(
            ai, new RecordingAuditLog(), NullLogger<AiInspectTransport>.Instance);

        var evt = new AuditEntry
        {
            EventType   = "Deployment.Failed",
            OccurredUtc = DateTimeOffset.UtcNow,
            UserDisplay = "ops",
            SpaceId     = WellKnown.DefaultSpaceId,
            SubjectType = "Deployment",
            SubjectName = "build-123",
            Details     = "exit code 137; OOMKilled",
        };

        await transport.DeliverAsync(DefaultSub(), evt, default);

        // The user message (last in the messages list) contains the
        // substituted prompt. Verify every placeholder got filled.
        var userMessage = ai.LastMessages![^1].Text ?? "";
        userMessage.Should().Contain("Deployment.Failed");
        userMessage.Should().Contain("build-123");
        userMessage.Should().Contain("exit code 137; OOMKilled",
            "the model sees the full Details field — that's where the " +
            "diagnostic signal lives");
    }

    [Fact]
    public async Task DeliverAsync_honours_custom_prompt_from_config()
    {
        var ai = new StubAi("ok");
        var transport = new AiInspectTransport(
            ai, new RecordingAuditLog(), NullLogger<AiInspectTransport>.Instance);

        var sub = new EventSubscription
        {
            Name                = "custom",
            SpaceId             = WellKnown.DefaultSpaceId,
            Transport           = SubscriptionTransport.AiInspect,
            TransportConfigJson = """
                {"prompt":"Respond with just the word KAIRO. Event: {EventType}"}
                """,
        };

        await transport.DeliverAsync(sub, NewEvent("Deployment.Failed"), default);

        var userMessage = ai.LastMessages![^1].Text ?? "";
        userMessage.Should().StartWith("Respond with just the word KAIRO",
            "the custom prompt wins over the default template");
        userMessage.Should().Contain("Deployment.Failed",
            "{EventType} placeholder works in custom prompts too");
    }

    [Fact]
    public async Task DeliverAsync_handles_disabled_AI_gracefully()
    {
        var ai = new ThrowingAi(new KrakenAiDisabledException("Provider=Disabled"));
        var auditLog = new RecordingAuditLog();
        var transport = new AiInspectTransport(
            ai, auditLog, NullLogger<AiInspectTransport>.Instance);

        var result = await transport.DeliverAsync(
            DefaultSub(), NewEvent("Deployment.Failed"), default);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("AI is disabled");
        auditLog.Recorded.Should().BeEmpty(
            "no diagnosis means no Diagnosis.Completed event; otherwise " +
            "subscribers chained on Diagnosis.* would get empty fires");
    }

    [Fact]
    public async Task DeliverAsync_handles_budget_exceeded_gracefully()
    {
        var ai = new ThrowingAi(new KrakenAiBudgetExceededException(
            monthToDateUsd: 42.00m, capUsd: 20.00m));
        var transport = new AiInspectTransport(
            ai, new RecordingAuditLog(), NullLogger<AiInspectTransport>.Instance);

        var result = await transport.DeliverAsync(
            DefaultSub(), NewEvent("Deployment.Failed"), default);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("budget");
    }

    [Fact]
    public async Task DeliverAsync_truncates_very_long_diagnoses()
    {
        // Defensive — the LLM is constrained via MaxOutputTokens, but a
        // misbehaving provider could ignore that. The transport applies
        // a hard cap before persisting.
        var hugeOutput = new string('x', AiInspectTransport.MaxStoredDiagnosisChars * 2);
        var ai = new StubAi(hugeOutput);
        var auditLog = new RecordingAuditLog();
        var transport = new AiInspectTransport(
            ai, auditLog, NullLogger<AiInspectTransport>.Instance);

        await transport.DeliverAsync(DefaultSub(), NewEvent("X"), default);

        var details = auditLog.Recorded.Single().Details!;
        details.Length.Should().BeLessThanOrEqualTo(
            AiInspectTransport.MaxStoredDiagnosisChars + 1, // +1 for the ellipsis
            "audit row size needs a defensive cap so a runaway model can't " +
            "blow up the audit table");
        details.Should().EndWith("…");
    }

    [Theory]
    [InlineData("{EventType}",   "Deployment.Failed", "Deployment.Failed")]
    [InlineData("{Subject}",     null,                "build-1")] // SubjectName takes precedence
    [InlineData("{Details}",     null,                "exit 137")]
    [InlineData("{OccurredUtc}", null,                "2026-01-01T00:00:00")]
    public void FillTemplate_substitutes_known_placeholders(
        string template, string? _, string expectedSubstring)
    {
        var e = new AuditEntry
        {
            EventType   = "Deployment.Failed",
            OccurredUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            UserDisplay = "t",
            SubjectType = "Deployment",
            SubjectName = "build-1",
            Details     = "exit 137",
        };
        AiInspectTransport.FillTemplate(template, e)
            .Should().Contain(expectedSubstring);
    }

    [Fact]
    public void FillTemplate_falls_back_for_missing_subject_fields()
    {
        var e = new AuditEntry { EventType = "X", OccurredUtc = DateTimeOffset.UtcNow, UserDisplay = "t" };
        AiInspectTransport.FillTemplate("{Subject}: {Details}", e)
            .Should().Be("(none): (empty)",
                "empty optional fields render as bracketed placeholders " +
                "so the LLM sees explicit absence rather than a blank gap");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static EventSubscription DefaultSub() => new()
    {
        Name                = "default",
        SpaceId             = WellKnown.DefaultSpaceId,
        Transport           = SubscriptionTransport.AiInspect,
        TransportConfigJson = "{}",
    };

    private static AuditEntry NewEvent(string type) => new()
    {
        EventType   = type,
        OccurredUtc = DateTimeOffset.UtcNow,
        UserDisplay = "test",
        SpaceId     = WellKnown.DefaultSpaceId,
    };

    private sealed class StubAi(string responseText) : IKrakenAi
    {
        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }

        public Task<KrakenAiCompletion> CompleteAsync(
            IReadOnlyList<ChatMessage> messages,
            KrakenAiFeature feature,
            KrakenAiRequestOptions? options = null,
            CancellationToken ct = default)
        {
            LastMessages = messages;
            return Task.FromResult(new KrakenAiCompletion(
                Text:             responseText,
                PromptTokens:     100,
                CompletionTokens: 50,
                Latency:          TimeSpan.FromMilliseconds(420),
                Provider:         "test",
                Model:            "test-model"));
        }

        public Task<TResult> CompleteAsync<TResult>(
            IReadOnlyList<ChatMessage> messages,
            KrakenAiFeature feature,
            KrakenAiRequestOptions? options = null,
            CancellationToken ct = default) where TResult : class
            => throw new NotImplementedException();

        public IAsyncEnumerable<string> StreamChatAsync(
            IReadOnlyList<ChatMessage> messages,
            KrakenAiFeature feature,
            KrakenAiRequestOptions? options = null,
            CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class ThrowingAi(Exception toThrow) : IKrakenAi
    {
        public Task<KrakenAiCompletion> CompleteAsync(
            IReadOnlyList<ChatMessage> messages, KrakenAiFeature feature,
            KrakenAiRequestOptions? options = null, CancellationToken ct = default)
            => throw toThrow;

        public Task<TResult> CompleteAsync<TResult>(
            IReadOnlyList<ChatMessage> messages, KrakenAiFeature feature,
            KrakenAiRequestOptions? options = null, CancellationToken ct = default)
            where TResult : class
            => throw toThrow;

        public IAsyncEnumerable<string> StreamChatAsync(
            IReadOnlyList<ChatMessage> messages, KrakenAiFeature feature,
            KrakenAiRequestOptions? options = null, CancellationToken ct = default)
            => throw toThrow;
    }

    private sealed class RecordingAuditLog : IAuditLog
    {
        public List<RecordedAuditEvent> Recorded { get; } = [];

        public Task RecordAsync(
            string eventType,
            string? subjectType = null,
            string? subjectId   = null,
            string? subjectName = null,
            string? details     = null,
            Guid? userId        = null,
            string? userDisplay = null,
            CancellationToken ct = default)
        {
            Recorded.Add(new RecordedAuditEvent(eventType, subjectType, subjectName, details));
            return Task.CompletedTask;
        }
    }

    public sealed record RecordedAuditEvent(
        string EventType, string? SubjectType, string? SubjectName, string? Details);
}
