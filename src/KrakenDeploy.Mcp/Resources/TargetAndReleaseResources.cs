using System.Text.Json;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Data.Services.Ai.ContextBuilders;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KrakenDeploy.Mcp.Resources;

/// <summary>
/// M11.B — the target-health + release-manifest resources. These were
/// deferred from the first Resources commit because they share the
/// TargetHealthBuilder / ReleaseContextBuilder that the tools also use —
/// landing the builders + both consumers together keeps each builder's
/// introduction in one place.
/// </summary>
[McpServerResourceType]
public sealed class TargetAndReleaseResources
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [McpServerResource(
        UriTemplate = "kraken://targets/{targetName}/health",
        Name        = "target_health",
        MimeType    = "application/json")]
    [System.ComponentModel.Description(
        "A deployment target's health snapshot: status, heartbeat, agent " +
        "info, roles, and last deployment result.")]
    public static async Task<TextResourceContents> GetTargetHealthAsync(
        TargetHealthBuilder builder,
        IAuditLog audit,
        string targetName,
        CancellationToken ct)
    {
        var uri = $"kraken://targets/{targetName}/health";
        var health = await builder.GetByNameAsync(targetName, ct).ConfigureAwait(false);
        if (health is null)
        {
            await McpAudit.ResourceReadAsync(audit, uri, "not-found", ct).ConfigureAwait(false);
            throw new McpException($"No target found with name '{targetName}'.");
        }
        await McpAudit.ResourceReadAsync(audit, uri, "ok", ct).ConfigureAwait(false);
        return new TextResourceContents
        {
            Uri      = uri,
            MimeType = "application/json",
            Text     = JsonSerializer.Serialize(health, Json),
        };
    }

    [McpServerResource(
        UriTemplate = "kraken://releases/{projectSlug}/{version}",
        Name        = "release_manifest",
        MimeType    = "application/json")]
    [System.ComponentModel.Description(
        "A release manifest: version, channel, release notes, step count, " +
        "and whether a variable snapshot is frozen. For the release's step " +
        "list, read the .../process sub-resource.")]
    public static async Task<TextResourceContents> GetReleaseManifestAsync(
        ReleaseContextBuilder builder,
        IAuditLog audit,
        string projectSlug,
        string version,
        CancellationToken ct)
    {
        var uri = $"kraken://releases/{projectSlug}/{version}";
        var manifest = await builder.GetAsync(projectSlug, version, ct).ConfigureAwait(false);
        if (manifest is null)
        {
            await McpAudit.ResourceReadAsync(audit, uri, "not-found", ct).ConfigureAwait(false);
            throw new McpException(
                $"No release '{version}' found for project '{projectSlug}'.");
        }
        await McpAudit.ResourceReadAsync(audit, uri, "ok", ct).ConfigureAwait(false);
        return new TextResourceContents
        {
            Uri      = uri,
            MimeType = "application/json",
            Text     = JsonSerializer.Serialize(manifest, Json),
        };
    }
}
