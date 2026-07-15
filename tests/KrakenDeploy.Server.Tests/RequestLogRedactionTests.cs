using FluentAssertions;
using KrakenDeploy.Server.Observability;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// A8/T1-12 — the request-log token redactor. Belt-and-braces for the agent
/// bearer token that SignalR delivers as <c>?access_token=</c>.
/// </summary>
public sealed class RequestLogRedactionTests
{
    [Fact]
    public void Redacts_the_access_token_value()
    {
        RequestLogRedaction.RedactTokens("/hubs/agent/negotiate?access_token=eyJhbGciOiJ.secret.sig")
            .Should().Be("/hubs/agent/negotiate?access_token=REDACTED");
    }

    [Fact]
    public void Preserves_other_query_parameters()
    {
        RequestLogRedaction.RedactTokens("/hubs/agent?id=abc&access_token=SECRET&v=2")
            .Should().Be("/hubs/agent?id=abc&access_token=REDACTED&v=2");
    }

    [Theory]
    [InlineData("/deployments/42")]
    [InlineData("/hubs/agent/negotiate")]
    [InlineData("")]
    public void Leaves_paths_without_a_token_unchanged(string path)
    {
        RequestLogRedaction.RedactTokens(path).Should().Be(path);
    }

    [Fact]
    public void Is_case_insensitive_on_the_parameter_name()
    {
        RequestLogRedaction.RedactTokens("/x?Access_Token=SECRET")
            .Should().Be("/x?Access_Token=REDACTED");
    }
}
