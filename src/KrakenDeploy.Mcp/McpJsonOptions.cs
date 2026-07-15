using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;

namespace KrakenDeploy.Mcp;

/// <summary>
/// JSON serialization options for the KrakenDeploy MCP surface.
/// <para>
/// Enum values are marshalled as their NAMES, matching the REST API
/// (<c>Program.ConfigureHttpJson</c>, commit 0cf2445). MCP tool output is
/// consumed by LLMs, where <c>"Failed"</c> is far more legible than <c>3</c>,
/// and one deployment's status must not read differently across the REST and
/// MCP surfaces. Before this, the SDK's default options emitted enums as
/// integers.
/// </para>
/// </summary>
internal static class McpJsonOptions
{
    /// <summary>
    /// Options for tool results marshalled by the MCP SDK — passed to
    /// <c>WithToolsFromAssembly</c>. Derived from
    /// <see cref="McpJsonUtilities.DefaultOptions"/> (the SDK's own default)
    /// so ONLY the enum representation changes: the SDK's Web defaults,
    /// <see cref="JsonIgnoreCondition.WhenWritingNull"/> null-omission and
    /// lenient number handling are preserved. <c>DeploymentSummaryDto.Status</c>
    /// — returned by <c>list_failed_deployments</c> and
    /// <c>get_deployment_log</c> — is the only enum on this surface today.
    /// </summary>
    internal static JsonSerializerOptions ForTools { get; } = BuildForTools();

    /// <summary>
    /// Options for resource payloads the handlers hand-serialize and return as
    /// text. Mirrors the pre-existing plain-Web behavior (nulls written) and
    /// adds the enum-name converter, so any enum that later appears in a
    /// resource payload is emitted as a name too. No resource carries an enum
    /// today; this keeps the whole surface consistent.
    /// </summary>
    internal static JsonSerializerOptions ForResources { get; } = BuildForResources();

    private static JsonSerializerOptions BuildForTools()
    {
        // Copy-construct: DefaultOptions is a frozen singleton; the copy is
        // mutable until the SDK consumes it.
        var options = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static JsonSerializerOptions BuildForResources()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
