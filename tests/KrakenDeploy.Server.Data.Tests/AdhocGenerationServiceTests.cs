using FluentAssertions;
using KrakenDeploy.Ai;
using KrakenDeploy.Server.Core.Domain.Ai;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data.Services.Ai.Adhoc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Tests for M11.E.2 — <see cref="AdhocGenerationService"/>. Pure: fake
/// <see cref="IKrakenAi"/>, no DB, no Postgres.
/// </summary>
public sealed class AdhocGenerationServiceTests
{
    private static AdhocSession NewSession(AdhocMode mode = AdhocMode.Readonly)
        => new()
        {
            Id                  = Guid.NewGuid(),
            Prompt              = "Check disk free space on the web tier",
            Mode                = mode,
            FrozenTargetSetJson = "[]",
            CreatedByDisplay    = "ops@laus.hr",
        };

    private static DeploymentTarget NewTarget(string name, string os, params string[] roles)
        => new()
        {
            Name            = name,
            OperatingSystem = os,
            Roles           = [.. roles],
            Status          = TargetStatus.Online,
        };

    [Fact]
    public async Task Generate_returns_structured_result_from_the_LLM()
    {
        var canned = new AdhocGenerationResult
        {
            Description         = "Reports free disk space on C: drive.",
            GeneratedScript     = "Get-PSDrive C | Select-Object Name, Free",
            ExpectedOutputShape = "A PSDrive object",
            RiskAssessment      = "None — read-only.",
            RequiresMutation    = false,
        };
        var ai = new FakeKrakenAi(canned);
        var svc = new AdhocGenerationService(ai, NullLogger<AdhocGenerationService>.Instance);

        var result = await svc.GenerateAsync(
            NewSession(),
            [NewTarget("web-01", "Windows Server 2022", "web")],
            sensitiveValues: null,
            CancellationToken.None);

        result.Should().BeSameAs(canned);
        ai.LastFeature.Should().Be(KrakenAiFeature.Adhoc);
        ai.LastSensitive.Should().BeNull();
    }

    [Fact]
    public async Task Generate_passes_sensitive_values_to_the_AI_wrapper()
    {
        var canned = new AdhocGenerationResult { GeneratedScript = "Get-Date" };
        var ai = new FakeKrakenAi(canned);
        var svc = new AdhocGenerationService(ai, NullLogger<AdhocGenerationService>.Instance);

        var sensitive = new Dictionary<string, string> { ["DbPassword"] = "supersecret" };
        await svc.GenerateAsync(NewSession(), [], sensitive, CancellationToken.None);

        ai.LastSensitive.Should().NotBeNull();
        ai.LastSensitive!["DbPassword"].Should().Be("supersecret");
    }

    [Fact]
    public async Task Generate_includes_target_context_in_the_user_prompt()
    {
        var ai = new FakeKrakenAi(new AdhocGenerationResult());
        var svc = new AdhocGenerationService(ai, NullLogger<AdhocGenerationService>.Instance);

        await svc.GenerateAsync(
            NewSession(),
            [
                NewTarget("web-01", "Windows Server 2022", "web"),
                NewTarget("db-01",  "Windows Server 2019", "database", "primary"),
            ],
            sensitiveValues: null,
            CancellationToken.None);

        ai.LastUserText.Should().Contain("web-01")
            .And.Contain("db-01")
            .And.Contain("Windows Server 2022")
            .And.Contain("database");
    }

    [Fact]
    public async Task Generate_signals_mode_to_the_LLM_via_the_system_prompt()
    {
        var ai = new FakeKrakenAi(new AdhocGenerationResult());
        var svc = new AdhocGenerationService(ai, NullLogger<AdhocGenerationService>.Instance);

        await svc.GenerateAsync(NewSession(AdhocMode.Mutating), [], null, CancellationToken.None);

        ai.LastSystemText.Should().Contain("MUTATING");
    }

    [Theory]
    [InlineData(typeof(KrakenAiDisabledException),        AdhocFeatureUnavailableReason.ProviderDisabled)]
    [InlineData(typeof(KrakenAiFeatureDisabledException), AdhocFeatureUnavailableReason.FeatureDisabled)]
    [InlineData(typeof(KrakenAiBudgetExceededException),  AdhocFeatureUnavailableReason.BudgetExceeded)]
    public async Task Generate_translates_each_AI_unavailability_to_a_typed_reason(
        Type thrown, AdhocFeatureUnavailableReason expectedReason)
    {
        var ai = new FakeKrakenAi(thrown: thrown);
        var svc = new AdhocGenerationService(ai, NullLogger<AdhocGenerationService>.Instance);

        var act = async () => await svc.GenerateAsync(
            NewSession(), [], null, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<AdhocFeatureUnavailableException>();
        ex.Which.Reason.Should().Be(expectedReason);
    }

    // ── Fake ────────────────────────────────────────────────────────────────

    private sealed class FakeKrakenAi : IKrakenAi
    {
        private readonly AdhocGenerationResult? _canned;
        private readonly Type? _thrown;

        public FakeKrakenAi(AdhocGenerationResult canned) { _canned = canned; }
        public FakeKrakenAi(Type thrown) { _thrown = thrown; }

        public string? LastUserText { get; private set; }
        public string? LastSystemText { get; private set; }
        public KrakenAiFeature LastFeature { get; private set; }
        public IReadOnlyDictionary<string, string>? LastSensitive { get; private set; }

        public Task<TResult> CompleteAsync<TResult>(
            IReadOnlyList<ChatMessage> messages, KrakenAiFeature feature,
            KrakenAiRequestOptions? options = null, CancellationToken ct = default)
            where TResult : class
        {
            LastFeature   = feature;
            LastSensitive = options?.SensitiveValues;
            LastUserText  = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text;
            LastSystemText = messages.LastOrDefault(m => m.Role == ChatRole.System)?.Text;

            if (_thrown is not null)
            {
                throw BuildException(_thrown);
            }
            return Task.FromResult((TResult)(object)_canned!);
        }

        private static Exception BuildException(Type t)
        {
            // Each KrakenAi exception has its own ctor shape; can't just
            // Activator.CreateInstance(string).
            if (t == typeof(KrakenAiDisabledException))
            {
                return new KrakenAiDisabledException("test-disabled");
            }
            if (t == typeof(KrakenAiFeatureDisabledException))
            {
                return new KrakenAiFeatureDisabledException("Adhoc");
            }
            if (t == typeof(KrakenAiBudgetExceededException))
            {
                return new KrakenAiBudgetExceededException(monthToDateUsd: 10m, capUsd: 5m);
            }
            throw new ArgumentException($"Unknown exception type {t}");
        }

        public Task<KrakenAiCompletion> CompleteAsync(
            IReadOnlyList<ChatMessage> messages, KrakenAiFeature feature,
            KrakenAiRequestOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<string> StreamChatAsync(
            IReadOnlyList<ChatMessage> messages, KrakenAiFeature feature,
            KrakenAiRequestOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
