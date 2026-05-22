using FluentAssertions;
using KrakenDeploy.Ai;

namespace KrakenDeploy.Ai.Tests;

/// <summary>
/// Tests for <see cref="AiCostCatalog"/> (M11.A.5). The default rate table
/// is anchored at the May 2026 list prices from each provider's public
/// pricing page — these tests pin those values so we notice if the table
/// drifts during a refactor.
/// </summary>
public sealed class AiCostCatalogTests
{
    [Fact]
    public void Knows_anthropic_sonnet_4_6_at_list_price()
    {
        var c = new AiCostCatalog();
        var rate = c.TryGetRate(KrakenAiProvider.Anthropic, "claude-sonnet-4.6");
        rate.Should().NotBeNull();
        rate!.InputUsdPer1k.Should().Be(0.003m,  "May 2026 list: $3/M input");
        rate.OutputUsdPer1k.Should().Be(0.015m,  "May 2026 list: $15/M output");
    }

    [Fact]
    public void Knows_anthropic_opus_4_7()
    {
        var c = new AiCostCatalog();
        var rate = c.TryGetRate(KrakenAiProvider.Anthropic, "claude-opus-4.7");
        rate!.InputUsdPer1k.Should().Be(0.005m);
        rate.OutputUsdPer1k.Should().Be(0.025m);
    }

    [Fact]
    public void Knows_openai_gpt_5_5_at_list_price()
    {
        var c = new AiCostCatalog();
        var rate = c.TryGetRate(KrakenAiProvider.OpenAI, "gpt-5.5");
        rate!.InputUsdPer1k.Should().Be(0.005m);
        rate.OutputUsdPer1k.Should().Be(0.030m);
    }

    [Fact]
    public void Knows_deepseek_chat_at_list_price()
    {
        var c = new AiCostCatalog();
        var rate = c.TryGetRate(KrakenAiProvider.DeepSeek, "deepseek-chat");
        rate!.InputUsdPer1k.Should().Be(0.00027m);
        rate.OutputUsdPer1k.Should().Be(0.00110m);
    }

    [Fact]
    public void Local_provider_always_returns_zero_rate_for_any_model()
    {
        var c = new AiCostCatalog();
        var rate = c.TryGetRate(KrakenAiProvider.LocalOpenAiCompatible, "any-random-model-tag");
        rate.Should().NotBeNull();
        rate!.InputUsdPer1k.Should().Be(0m);
        rate.OutputUsdPer1k.Should().Be(0m);
    }

    [Fact]
    public void Model_lookup_is_case_insensitive()
    {
        var c = new AiCostCatalog();
        c.TryGetRate(KrakenAiProvider.Anthropic, "CLAUDE-SONNET-4.6").Should().NotBeNull();
        c.TryGetRate(KrakenAiProvider.Anthropic, "claude-sonnet-4.6").Should().NotBeNull();
    }

    [Fact]
    public void Unknown_model_returns_null_so_callers_can_log_a_warning()
    {
        var c = new AiCostCatalog();
        c.TryGetRate(KrakenAiProvider.Anthropic, "claude-future-model-not-in-table")
            .Should().BeNull();
        c.TryGetRate(KrakenAiProvider.OpenAI, "gpt-7-not-yet-released")
            .Should().BeNull();
    }
}
