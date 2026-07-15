using System.Text.Json;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KrakenDeploy.Mcp.Resources;

/// <summary>
/// M11.B — drill-down resources returning a single step's FULL, unredacted
/// Config dictionary. The process resources (<see cref="ProcessResources"/>)
/// emit a curated summary per step plus a <c>fullConfigUri</c> pointing
/// here; the AI reads this only when it needs the complete config to
/// troubleshoot a specific step.
/// <para>
/// Step is addressed by zero-based index into the SortOrder-ordered
/// process / snapshot — the same index the process resource emits, so the
/// AI can round-trip "summary → full config" without a name lookup.
/// </para>
/// </summary>
[McpServerResourceType]
public sealed class StepConfigResources
{
    private static readonly JsonSerializerOptions Json = McpJsonOptions.ForResources;

    [McpServerResource(
        UriTemplate = "kraken://projects/{projectSlug}/process/steps/{index}/config",
        Name        = "project_step_config",
        MimeType    = "application/json")]
    [System.ComponentModel.Description(
        "The complete, unredacted config dictionary for one step of a " +
        "project's live process, addressed by zero-based index. Use when the " +
        "curated summary in the process resource isn't enough to diagnose a step.")]
    public static async Task<TextResourceContents> GetProjectStepConfigAsync(
        IDbContextFactory<KrakenDbContext> dbFactory,
        IPermissionEvaluator permissions,
        IHttpContextAccessor httpContext,
        IAuditLog audit,
        string projectSlug,
        int index,
        CancellationToken ct)
    {
        await McpToolAuth.EnsureAsync(
            permissions, httpContext, "project_step_config", Permission.ProcessView, audit, ct)
            .ConfigureAwait(false);
        var uri = $"kraken://projects/{projectSlug}/process/steps/{index}/config";
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var project = await db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Slug == projectSlug, ct).ConfigureAwait(false);
        if (project is null)
        {
            await McpAudit.ResourceReadAsync(audit, uri, "not-found", ct).ConfigureAwait(false);
            throw new McpException($"No project found with slug '{projectSlug}'.");
        }

        var rawConfigs = await db.Processes.AsNoTracking()
            .Where(p => p.OwnerKind == ProcessOwnerKind.Project && p.OwnerId == project.Id)
            .SelectMany(p => p.Steps)
            .OrderBy(s => s.SortOrder)
            .Select(s => s.Config)
            .ToListAsync(ct).ConfigureAwait(false);

        var configs = rawConfigs
            .Select(c => (IReadOnlyDictionary<string, string>)c)
            .ToList();

        return await BuildConfigContentsAsync(audit, uri, configs, index, ct).ConfigureAwait(false);
    }

    [McpServerResource(
        UriTemplate = "kraken://releases/{projectSlug}/{version}/steps/{index}/config",
        Name        = "release_step_config",
        MimeType    = "application/json")]
    [System.ComponentModel.Description(
        "The complete, unredacted config dictionary for one step of a release's " +
        "frozen process snapshot, addressed by zero-based index.")]
    public static async Task<TextResourceContents> GetReleaseStepConfigAsync(
        IDbContextFactory<KrakenDbContext> dbFactory,
        IPermissionEvaluator permissions,
        IHttpContextAccessor httpContext,
        IAuditLog audit,
        string projectSlug,
        string version,
        int index,
        CancellationToken ct)
    {
        await McpToolAuth.EnsureAsync(
            permissions, httpContext, "release_step_config", Permission.ProcessView, audit, ct)
            .ConfigureAwait(false);
        var uri = $"kraken://releases/{projectSlug}/{version}/steps/{index}/config";
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var release = await db.Releases.AsNoTracking()
            .Include(r => r.Project)
            .FirstOrDefaultAsync(r => r.Project.Slug == projectSlug && r.Version == version, ct)
            .ConfigureAwait(false);
        if (release is null)
        {
            await McpAudit.ResourceReadAsync(audit, uri, "not-found", ct).ConfigureAwait(false);
            throw new McpException(
                $"No release '{version}' found for project '{projectSlug}'.");
        }

        var configs = release.ProcessSnapshot
            .OrderBy(s => s.SortOrder)
            .Select(s => (IReadOnlyDictionary<string, string>)s.Config)
            .ToList();

        return await BuildConfigContentsAsync(audit, uri, configs, index, ct).ConfigureAwait(false);
    }

    private static async Task<TextResourceContents> BuildConfigContentsAsync(
        IAuditLog audit,
        string uri,
        List<IReadOnlyDictionary<string, string>> configs,
        int index,
        CancellationToken ct)
    {
        if (index < 0 || index >= configs.Count)
        {
            await McpAudit.ResourceReadAsync(audit, uri, "not-found", ct).ConfigureAwait(false);
            throw new McpException(
                $"Step index {index} is out of range (process has {configs.Count} step(s)).");
        }
        await McpAudit.ResourceReadAsync(audit, uri, "ok", ct).ConfigureAwait(false);
        return new TextResourceContents
        {
            Uri      = uri,
            MimeType = "application/json",
            Text     = JsonSerializer.Serialize(configs[index], Json),
        };
    }
}
