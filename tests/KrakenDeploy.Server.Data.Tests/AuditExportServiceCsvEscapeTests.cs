using FluentAssertions;
using KrakenDeploy.Server.Data.Services;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Unit tests for <see cref="AuditExportService.CsvEscape"/>. CSV escaping
/// is the audit-export class's main bug-bait — a missed quote or newline
/// breaks every row that follows in Excel. RFC 4180 rules:
///
///   - empty / null → empty field
///   - no special chars → unchanged
///   - comma OR newline OR carriage-return → wrap in double quotes
///   - quote → wrap in double quotes AND double internal quotes
/// </summary>
public sealed class AuditExportServiceCsvEscapeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Null_or_empty_yields_empty(string? input)
    {
        AuditExportService.CsvEscape(input).Should().Be("");
    }

    [Theory]
    [InlineData("plain",                 "plain")]
    [InlineData("Project.Created",       "Project.Created")]
    [InlineData("LAUS d.o.o.",           "LAUS d.o.o.")]
    [InlineData("with-dashes_and 1",     "with-dashes_and 1")]
    public void Safe_values_are_not_quoted(string input, string expected)
    {
        AuditExportService.CsvEscape(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("a,b",              "\"a,b\"")]
    [InlineData("hello, world",     "\"hello, world\"")]
    [InlineData(",leading",         "\",leading\"")]
    [InlineData("trailing,",        "\"trailing,\"")]
    public void Comma_triggers_quoting(string input, string expected)
    {
        AuditExportService.CsvEscape(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("line\nbreak",      "\"line\nbreak\"")]
    [InlineData("carriage\rreturn", "\"carriage\rreturn\"")]
    [InlineData("crlf\r\nhere",     "\"crlf\r\nhere\"")]
    public void Newlines_trigger_quoting(string input, string expected)
    {
        // The whole point of CSV quoting around newlines is to keep the row
        // on one logical line for parsers. Without this every multi-line
        // Details field (we have a few — license summaries, deployment
        // failures) would corrupt the rest of the file.
        AuditExportService.CsvEscape(input).Should().Be(expected);
    }

    [Fact]
    public void Quote_is_escaped_by_doubling()
    {
        AuditExportService.CsvEscape("say \"hi\"")
            .Should().Be("\"say \"\"hi\"\"\"",
                "RFC 4180: internal quotes are doubled AND the field is " +
                "wrapped in outer quotes");
    }

    [Fact]
    public void Quote_alone_still_quotes_the_field()
    {
        AuditExportService.CsvEscape("\"")
            .Should().Be("\"\"\"\"",
                "outer pair plus the doubled internal one");
    }

    [Fact]
    public void Comma_and_quote_together()
    {
        AuditExportService.CsvEscape("a, \"b\", c")
            .Should().Be("\"a, \"\"b\"\", c\"");
    }

    [Theory]
    [InlineData("a\tb")]      // tab is NOT a CSV special char
    [InlineData("a;b")]       // semicolon either (we're not in EU-Excel mode)
    [InlineData("a|b")]
    public void Other_punctuation_does_not_trigger_quoting(string input)
    {
        // Defensive: a buggy "quote everything with a separator" rule would
        // wrap these too, bloating the file and confusing parsers that
        // genuinely use these chars as separators.
        AuditExportService.CsvEscape(input).Should().Be(input);
    }
}
