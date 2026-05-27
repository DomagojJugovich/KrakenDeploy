using KrakenDeploy.Server.Core.Domain.Audit;

namespace KrakenDeploy.Mcp;

/// <summary>
/// Thin helper that writes the <see cref="AuditEventType.McpResourceRead"/> /
/// <see cref="AuditEventType.McpToolInvoked"/> forensic rows. Centralised so
/// every resource + tool emits an identical event shape — the subject is
/// the resource URI / tool name, and the detail carries a short outcome
/// note. Best-effort: an audit hiccup must never fail the MCP call, so the
/// caller wraps it in try/catch is unnecessary — IAuditLog.RecordAsync is
/// the same primitive the rest of the server treats as reliable.
/// </summary>
internal static class McpAudit
{
    public static Task ResourceReadAsync(
        IAuditLog audit, string uri, string outcome, CancellationToken ct)
        => audit.RecordAsync(
            AuditEventType.McpResourceRead,
            subjectType: "McpResource",
            subjectId:   uri,
            details:     $"Resource={uri}, Outcome={outcome}",
            ct:          ct);

    public static Task ToolInvokedAsync(
        IAuditLog audit, string toolName, string argsSummary, string outcome, CancellationToken ct)
        => audit.RecordAsync(
            AuditEventType.McpToolInvoked,
            subjectType: "McpTool",
            subjectId:   toolName,
            details:     $"Tool={toolName}, Args=[{argsSummary}], Outcome={outcome}",
            ct:          ct);
}
