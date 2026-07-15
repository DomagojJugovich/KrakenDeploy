using System.Text.Json;
using FluentAssertions;
using KrakenDeploy.Mcp;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Data.Services.Ai.ContextBuilders;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Pins the MCP JSON contract (<see cref="McpJsonOptions"/>). Tool results are
/// marshalled by the SDK with <see cref="McpJsonOptions.ForTools"/>; before this
/// the SDK's default options emitted enums as INTEGERS, so a deployment's status
/// read as <c>3</c> over MCP while the REST API (after commit 0cf2445) sent
/// <c>"Failed"</c>. These are fast, deterministic proxies for the wire; the
/// end-to-end SDK round-trip is asserted in
/// <c>McpIntegrationTests.Failed_deployment_tool_serializes_status_as_enum_name</c>.
/// </summary>
public sealed class McpJsonOptionsTests
{
    private static DeploymentSummaryDto Sample(DeploymentStatus status) => new(
        Id:              Guid.NewGuid(),
        ProjectName:     "Argosy",
        ProjectSlug:     "argosy",
        ReleaseVersion:  "1.0",
        EnvironmentName: "Production",
        TargetNames:     ["web-01"],
        Status:          status,
        StartedUtc:      DateTimeOffset.UtcNow,
        CompletedUtc:    null);

    [Theory]
    [InlineData(DeploymentStatus.Failed)]
    [InlineData(DeploymentStatus.SucceededWithWarnings)]
    [InlineData(DeploymentStatus.Succeeded)]
    public void ForTools_serializes_deployment_status_as_its_name(DeploymentStatus status)
    {
        var json = JsonSerializer.Serialize(Sample(status), McpJsonOptions.ForTools);

        json.Should().Contain($"\"{status}\"");
        json.Should().NotContain($"\"status\":{(int)status}",
            because: "MCP tool output must carry enum names, matching the REST wire");
    }

    [Fact]
    public void ForTools_preserves_the_sdk_null_omission()
    {
        // Regression guard: ForTools derives from McpJsonUtilities.DefaultOptions,
        // whose WhenWritingNull omits null properties. Only the enum representation
        // should change — not the SDK's null handling.
        var json = JsonSerializer.Serialize(Sample(DeploymentStatus.Failed), McpJsonOptions.ForTools);

        json.Should().NotContain("completedUtc",
            because: "the SDK default omits nulls; CompletedUtc is null here");
    }

    [Fact]
    public void ForTools_still_reads_a_numeric_enum_token()
    {
        // Back-compat on the read path (the SDK also binds tool arguments with
        // these options): a numeric enum token must still deserialize.
        var dto = JsonSerializer.Deserialize<DeploymentSummaryDto>(
            "{\"id\":\"" + Guid.Empty + "\",\"projectName\":\"\",\"projectSlug\":\"\"," +
            "\"releaseVersion\":\"\",\"environmentName\":\"\",\"targetNames\":[]," +
            "\"status\":3,\"startedUtc\":null,\"completedUtc\":null}",
            McpJsonOptions.ForTools);

        dto!.Status.Should().Be(DeploymentStatus.Failed);
    }

    [Fact]
    public void ForResources_serializes_enums_as_names()
    {
        // No resource payload carries an enum today, but the converter is present
        // so a future one is emitted as a name too.
        var json = JsonSerializer.Serialize(
            new { Status = DeploymentStatus.Cancelled }, McpJsonOptions.ForResources);

        json.Should().Contain("\"Cancelled\"").And.NotContain($"{(int)DeploymentStatus.Cancelled}");
    }
}
