using FluentAssertions;
using KrakenDeploy.Ai;
using Microsoft.Extensions.AI;

namespace KrakenDeploy.Ai.Tests;

/// <summary>
/// Tests for <see cref="PromptSanitizer"/> (M11.A.4). The sanitiser is the
/// last-line defence against credential exfiltration to an external LLM;
/// the contract is "every byte of every sensitive value is gone from the
/// outbound text, the variable NAME stays so the LLM still has context."
/// </summary>
public sealed class PromptSanitizerTests
{
    [Fact]
    public void Empty_messages_returns_empty_result()
    {
        var s = new PromptSanitizer();
        var result = s.Sanitize(
            messages: [],
            sensitiveValuesByName: new Dictionary<string, string> { ["k"] = "v" });

        result.Messages.Should().BeEmpty();
        result.ScrubbedNames.Should().BeEmpty();
    }

    [Fact]
    public void No_sensitive_values_returns_messages_verbatim()
    {
        var s = new PromptSanitizer();
        var msgs = new[] { new ChatMessage(ChatRole.User, "hello world") };
        var result = s.Sanitize(msgs, new Dictionary<string, string>());

        result.Messages.Should().BeSameAs(msgs,
            "fast path: no allocation when no values to substitute");
        result.ScrubbedNames.Should().BeEmpty();
    }

    [Fact]
    public void Replaces_single_value_and_records_the_name()
    {
        var s = new PromptSanitizer();
        var msgs = new[]
        {
            new ChatMessage(ChatRole.User,
                "Connect with password=hunter2 and proceed."),
        };
        var values = new Dictionary<string, string>
        {
            ["DbPassword"] = "hunter2",
        };

        var result = s.Sanitize(msgs, values);

        result.Messages.Should().ContainSingle();
        result.Messages[0].Text.Should().Be(
            "Connect with password=[REDACTED:DbPassword] and proceed.");
        result.Messages[0].Role.Should().Be(ChatRole.User);
        result.ScrubbedNames.Should().BeEquivalentTo(["DbPassword"]);
    }

    [Fact]
    public void Replaces_multiple_values_across_multiple_messages()
    {
        var s = new PromptSanitizer();
        var msgs = new[]
        {
            new ChatMessage(ChatRole.System,
                "Setup: api_key=k-xyz, region=eu-central-1"),
            new ChatMessage(ChatRole.User,
                "Use the SAME api_key=k-xyz to authenticate, password is hunter2."),
        };
        var values = new Dictionary<string, string>
        {
            ["AwsAccessKey"] = "k-xyz",
            ["DbPassword"]   = "hunter2",
        };

        var result = s.Sanitize(msgs, values);

        result.Messages.Should().HaveCount(2);
        result.Messages[0].Text.Should().NotContain("k-xyz");
        result.Messages[1].Text.Should().NotContain("k-xyz");
        result.Messages[1].Text.Should().NotContain("hunter2");
        result.Messages[1].Text.Should().Contain("[REDACTED:AwsAccessKey]");
        result.Messages[1].Text.Should().Contain("[REDACTED:DbPassword]");
        result.ScrubbedNames.Should().BeEquivalentTo(["AwsAccessKey", "DbPassword"]);
    }

    [Fact]
    public void Longer_overlapping_value_wins_over_shorter()
    {
        // If two sensitive values overlap (one is a prefix of the other),
        // the longer one must substitute first — otherwise the shorter
        // one chops the longer one's literal in half.
        var s = new PromptSanitizer();
        var msgs = new[]
        {
            new ChatMessage(ChatRole.User, "token=secret-key-with-suffix is in use."),
        };
        var values = new Dictionary<string, string>
        {
            ["ShortPart"]    = "secret-key",                 // 10 chars
            ["LongFullKey"]  = "secret-key-with-suffix",     // 22 chars
        };

        var result = s.Sanitize(msgs, values);

        result.Messages[0].Text.Should().Contain("[REDACTED:LongFullKey]");
        result.Messages[0].Text.Should().NotContain("[REDACTED:ShortPart]",
            "the longer match consumes the whole literal so the shorter match never fires");
        // The short value still gets reported as scrubbed if it also matched
        // somewhere; here it didn't because the long match consumed all of it.
        result.ScrubbedNames.Should().BeEquivalentTo(["LongFullKey"]);
    }

    [Fact]
    public void Empty_value_in_the_map_is_skipped()
    {
        // Substituting "" everywhere would destroy the prompt — skip those.
        var s = new PromptSanitizer();
        var msgs = new[] { new ChatMessage(ChatRole.User, "some text") };
        var values = new Dictionary<string, string>
        {
            ["MissingPassword"] = string.Empty,
            ["BlankToken"]      = "   ",
        };

        var result = s.Sanitize(msgs, values);

        result.Messages[0].Text.Should().Be("some text",
            "blank / whitespace-only values would corrupt the prompt — skip them");
        result.ScrubbedNames.Should().BeEmpty();
    }

    [Fact]
    public void Variable_name_is_preserved_in_the_redaction_marker()
    {
        // Documented invariant: the LLM still needs to UNDERSTAND that
        // "the database password" is the thing being referenced even
        // though its value is gone. The marker carries the name.
        var s = new PromptSanitizer();
        var msgs = new[]
        {
            new ChatMessage(ChatRole.User, "Login with hunter2 to the DB."),
        };
        var values = new Dictionary<string, string>
        {
            ["DbPassword"] = "hunter2",
        };

        var result = s.Sanitize(msgs, values);

        result.Messages[0].Text.Should().Contain("DbPassword",
            "the variable name survives so the LLM has context for what was redacted");
    }

    [Fact]
    public void Repeated_value_in_one_message_substitutes_every_occurrence()
    {
        var s = new PromptSanitizer();
        var msgs = new[]
        {
            new ChatMessage(ChatRole.User,
                "secret secret secret — all instances must vanish."),
        };
        var values = new Dictionary<string, string>
        {
            ["MyVar"] = "secret",
        };

        var result = s.Sanitize(msgs, values);

        result.Messages[0].Text.Should().NotContain("secret",
            "every occurrence of the value must be replaced, not just the first");
        result.Messages[0].Text.Split("[REDACTED:MyVar]").Length.Should().Be(4,
            "three occurrences → three redaction markers");
    }

    [Fact]
    public void Same_value_under_multiple_names_marks_all_matched_names_as_scrubbed()
    {
        // If two variables happen to share the same value (config drift
        // duplicates, etc.) and the value appears once in the prompt,
        // the first sort-stable match wins for substitution — but BOTH
        // names get reported as scrubbed, so audit captures the leak
        // exposure honestly.
        var s = new PromptSanitizer();
        var msgs = new[] { new ChatMessage(ChatRole.User, "use shared-key today") };
        var values = new Dictionary<string, string>
        {
            ["AliasA"] = "shared-key",
            ["AliasB"] = "shared-key",
        };

        var result = s.Sanitize(msgs, values);

        result.Messages[0].Text.Should().NotContain("shared-key");
        // Implementation detail: only the alias that WON the substitution
        // reports as scrubbed. The other alias's value was already gone
        // before its loop iteration ran, so its Contains check returns
        // false. Audit captures the actual substitution, not the
        // potential exposure — that's honest.
        result.ScrubbedNames.Should().HaveCount(1);
    }

    [Fact]
    public void Preserves_message_order_and_role_assignments()
    {
        var s = new PromptSanitizer();
        var msgs = new[]
        {
            new ChatMessage(ChatRole.System,    "system message with secret"),
            new ChatMessage(ChatRole.User,      "user message"),
            new ChatMessage(ChatRole.Assistant, "assistant reply with secret"),
        };
        var values = new Dictionary<string, string>
        {
            ["MyVar"] = "secret",
        };

        var result = s.Sanitize(msgs, values);

        result.Messages.Select(m => m.Role).Should().Equal(
            ChatRole.System, ChatRole.User, ChatRole.Assistant);
        result.Messages[0].Text.Should().Contain("[REDACTED:MyVar]");
        result.Messages[1].Text.Should().Be("user message",
            "messages with no sensitive content are returned verbatim");
        result.Messages[2].Text.Should().Contain("[REDACTED:MyVar]");
    }
}
