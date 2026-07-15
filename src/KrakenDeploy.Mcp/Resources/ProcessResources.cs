using System.Text.Json;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Data.Services.Ai.ContextBuilders;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KrakenDeploy.Mcp.Resources;

/// <summary>
/// M11.B — read-only resources exposing a project's deployment process
/// (live + frozen-snapshot forms) as LLM-shaped JSON. Both delegate to the
/// shared <see cref="ProcessContextBuilder"/>, so the curated config
/// summaries + parent-name resolution + server-side classification are
/// identical to what the M11.C diagnosis job sees.
/// <para>
/// Each step in the returned JSON carries a <c>fullConfigUri</c> the AI can
/// read when it needs the unredacted Config — see
/// <see cref="StepConfigResources"/>.
/// </para>
/// </summary>
[McpServerResourceType]
public sealed class ProcessResources
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [McpServerResource(
        UriTemplate = "kraken://projects/{projectSlug}/process",
        Name        = "project_process",
        MimeType    = "application/json")]
    [System.ComponentModel.Description(
        "The live (current, editable) deployment process for a project, as a " +
        "slim step list. Each step carries a curated config summary + a " +
        "fullConfigUri for drilling into the complete config when needed.")]
    public static async Task<TextResourceContents> GetProjectProcessAsync(
        ProcessContextBuilder builder,
        IPermissionEvaluator permissions,
        IHttpContextAccessor httpContext,
        IAuditLog audit,
        string projectSlug,
        CancellationToken ct)
    {
        await McpToolAuth.EnsureAsync(
            permissions, httpContext, "project_process", Permission.ProcessView, audit, ct)
            .ConfigureAwait(false);
        var uri = $"kraken://projects/{projectSlug}/process";
        var ctx = await builder.BuildForProjectAsync(projectSlug, ct).ConfigureAwait(false);
        if (ctx is null)
        {
            await McpAudit.ResourceReadAsync(audit, uri, "not-found", ct).ConfigureAwait(false);
            throw new McpException($"No project found with slug '{projectSlug}'.");
        }
        await McpAudit.ResourceReadAsync(audit, uri, "ok", ct).ConfigureAwait(false);
        return new TextResourceContents
        {
            Uri      = uri,
            MimeType = "application/json",
            Text     = JsonSerializer.Serialize(ctx, Json),
        };
    }

    [McpServerResource(
        UriTemplate = "kraken://releases/{projectSlug}/{version}/process",
        Name        = "release_process",
        MimeType    = "application/json")]
    [System.ComponentModel.Description(
        "The frozen process snapshot for a specific release — what actually " +
        "deploys when this release runs, regardless of later edits to the " +
        "live process. Same slim shape as the live process resource.")]
    public static async Task<TextResourceContents> GetReleaseProcessAsync(
        ProcessContextBuilder builder,
        IPermissionEvaluator permissions,
        IHttpContextAccessor httpContext,
        IAuditLog audit,
        string projectSlug,
        string version,
        CancellationToken ct)
    {
        await McpToolAuth.EnsureAsync(
            permissions, httpContext, "release_process", Permission.ProcessView, audit, ct)
            .ConfigureAwait(false);
        var uri = $"kraken://releases/{projectSlug}/{version}/process";
        var ctx = await builder.BuildForReleaseAsync(projectSlug, version, ct).ConfigureAwait(false);
        if (ctx is null)
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
            Text     = JsonSerializer.Serialize(ctx, Json),
        };
    }
}
