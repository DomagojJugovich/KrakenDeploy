using System.Text;
using System.Text.Json;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Data;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KrakenDeploy.Mcp.Resources;

/// <summary>
/// M11.B — the full deployment log as newline-delimited JSON (one object
/// per log line: sequence, timestamp, level, message). ndjson rather than a
/// single JSON array so an AI client can stream / chunk a large log without
/// parsing the whole thing, and so a tail is trivially the last N lines.
/// </summary>
[McpServerResourceType]
public sealed class DeploymentLogResource
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [McpServerResource(
        UriTemplate = "kraken://deployments/{deploymentId}/log",
        Name        = "deployment_log",
        MimeType    = "application/x-ndjson")]
    [System.ComponentModel.Description(
        "The complete log for a deployment as newline-delimited JSON — one " +
        "object per line with sequence, timestamp, level, and message. " +
        "Ordered by sequence. Empty body when the deployment has no log yet.")]
    public static async Task<TextResourceContents> GetDeploymentLogAsync(
        IDbContextFactory<KrakenDbContext> dbFactory,
        IAuditLog audit,
        Guid deploymentId,
        CancellationToken ct)
    {
        var uri = $"kraken://deployments/{deploymentId}/log";
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var exists = await db.Deployments.AsNoTracking()
            .AnyAsync(d => d.Id == deploymentId, ct).ConfigureAwait(false);
        if (!exists)
        {
            await McpAudit.ResourceReadAsync(audit, uri, "not-found", ct).ConfigureAwait(false);
            throw new McpException($"No deployment found with id '{deploymentId}'.");
        }

        var lines = await db.DeploymentLogEntries.AsNoTracking()
            .Where(l => l.DeploymentId == deploymentId)
            .OrderBy(l => l.Sequence)
            .Select(l => new { l.Sequence, l.Timestamp, l.Level, l.Message })
            .ToListAsync(ct).ConfigureAwait(false);

        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            sb.AppendLine(JsonSerializer.Serialize(line, Json));
        }

        await McpAudit.ResourceReadAsync(audit, uri, $"ok ({lines.Count} lines)", ct)
            .ConfigureAwait(false);
        return new TextResourceContents
        {
            Uri      = uri,
            MimeType = "application/x-ndjson",
            Text     = sb.ToString(),
        };
    }
}
