using System.Text;
using FluentAssertions;
using KrakenDeploy.Execution;
using KrakenDeploy.Server.Core.Domain.Audit;

namespace KrakenDeploy.Server.Core.Tests;

/// <summary>
/// Bounding free text that reaches <c>AuditEntry.Details</c> and, through the subscription
/// poller, the webhook / e-mail / AI-inspect transports.
/// <para>
/// The surrogate cases are the point. Slicing by UTF-16 code unit can leave a lone high
/// surrogate, and Npgsql's parameter writer uses <c>EncoderExceptionFallback</c> — so persisting
/// such a string THROWS instead of storing it, turning an over-long detail into a 500 that loses
/// the whole audit row. These tests encode with the same strict fallback Npgsql uses, so they
/// fail the same way the database would.
/// </para>
/// </summary>
public class TextBudgetTests
{
    // The strict encoder: exactly what Npgsql's write buffer uses, so a lone surrogate throws
    // here for the same reason it throws on SaveChangesAsync.
    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false,
                                                     throwOnInvalidBytes: true);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(64)]
    public void A_cut_that_lands_inside_a_surrogate_pair_still_encodes(int max)
    {
        // An emoji (U+1F600) is TWO UTF-16 code units. Build strings so the cut point lands on
        // every offset within and around a pair, and assert every result is encodable.
        for (var lead = 0; lead <= max + 2; lead++)
        {
            var value = new string('a', lead) + "\U0001F600" + new string('b', 40);

            var trimmed = TextBudget.Trim(value, max)!;

            trimmed.Length.Should().BeLessThanOrEqualTo(max,
                "the cap must actually cap, marker included");
            var act = () => Strict.GetBytes(trimmed);
            act.Should().NotThrow<EncoderFallbackException>(
                $"lead={lead}, max={max} must not leave a lone surrogate — Npgsql would refuse " +
                "to persist it and the audit row would be lost");
        }
    }

    [Fact]
    public void The_audit_entity_setter_bounds_and_stays_encodable()
    {
        // The reachable production path: POST /api/agents/update-status carries an unbounded
        // Detail from the agent, which is interpolated into Details.
        var entry = new AuditEntry
        {
            Details = new string('x', AuditEntry.MaxDetailsLength - 1) + "\U0001F600",
        };

        entry.Details!.Length.Should().BeLessThanOrEqualTo(AuditEntry.MaxDetailsLength);
        var act = () => Strict.GetBytes(entry.Details);
        act.Should().NotThrow<EncoderFallbackException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short enough")]
    public void Text_that_already_fits_is_returned_unchanged(string? value)
        => TextBudget.Trim(value, 64).Should().Be(value);

    [Fact]
    public void Trimmed_text_is_marked_so_a_reader_can_tell()
    {
        var trimmed = TextBudget.Trim(new string('a', 100), 10)!;
        trimmed.Should().HaveLength(10);
        trimmed[^1].Should().Be(TextBudget.Ellipsis);
    }

    [Fact]
    public void Describe_names_the_original_length()
    {
        TextBudget.Describe(new string('a', 30_000), 24)
            .Should().Contain("30000 chars");

        TextBudget.Describe("4", 24).Should().Be("\"4\"",
            "a value that fits is quoted verbatim, with no misleading length suffix");
    }

    [Fact]
    public void A_max_below_one_is_a_programming_error()
    {
        var act = () => TextBudget.Trim("abc", 0);
        act.Should().Throw<ArgumentOutOfRangeException>(
            "there is no room for the marker, so silently returning something shorter than " +
            "asked for would be worse than failing");
    }
}
