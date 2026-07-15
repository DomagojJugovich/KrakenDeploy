using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using KrakenDeploy.Server.Core.Domain.StepTemplates;
using KrakenDeploy.Server.Data.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Caches step-template metadata from the OctopusDeploy/Library GitHub repo
/// in the local <c>step_template_catalog</c> table. Used by the
/// <c>/step-templates/community</c> catalog browser. Refreshed hourly by a
/// Hangfire recurring job and on demand via the "Refresh" button.
/// <para>
/// Strategy:
/// <list type="number">
///   <item>One GitHub API call to the Git Trees endpoint returns every blob's
///         path + SHA in the master branch (single request, cheap on the
///         60-req/hr unauthenticated rate limit).</item>
///   <item>For each <c>step-templates/*.json</c> path whose SHA has changed
///         since the last sync, fetch the raw file via
///         <c>raw.githubusercontent.com</c> (does NOT count against the API
///         rate limit) and extract metadata.</item>
///   <item>Upsert by <c>CommunityActionTemplateId</c>. Delete catalog rows
///         whose path no longer appears in the tree (orphans from deletions
///         upstream).</item>
/// </list>
/// </para>
/// </summary>
public class StepTemplateCatalogService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    IHttpClientFactory httpClientFactory,
    StepTemplateService stepTemplateService,
    IOptions<SsrfOptions> ssrfOptions,
    ILogger<StepTemplateCatalogService> logger)
{
    private const string Owner = "OctopusDeploy";
    private const string Repo  = "Library";
    private const string Branch = "master";
    private const string SubDir = "step-templates";

    /// <summary>Named <see cref="HttpClient"/> registered in <c>Program.cs</c>.</summary>
    public const string HttpClientName = "kraken.github";

    // ── Queries ────────────────────────────────────────────────────────────

    public async Task<List<StepTemplateCatalogEntry>> GetAllAsync(
        string? category = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var q = db.StepTemplateCatalog.AsQueryable();
        if (!string.IsNullOrWhiteSpace(category))
        {
            q = q.Where(e => e.Category == category);
        }
        return await q.OrderBy(e => e.Name).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> GetCountAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.StepTemplateCatalog.CountAsync(ct).ConfigureAwait(false);
    }

    public async Task<DateTimeOffset?> GetLastSyncAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (!await db.StepTemplateCatalog.AnyAsync(ct).ConfigureAwait(false))
        {
            return null;
        }
        return await db.StepTemplateCatalog
            .MaxAsync(e => (DateTimeOffset?)e.LastSyncedUtc, ct).ConfigureAwait(false);
    }

    // ── Install ────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches the full JSON for a catalogued entry and installs it as a real
    /// <see cref="StepTemplate"/> via <see cref="StepTemplateService.ImportFromJsonAsync"/>
    /// with <see cref="StepTemplateSource.CommunityLibrary"/>.
    /// </summary>
    public async Task<StepTemplate> InstallAsync(Guid catalogEntryId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entry = await db.StepTemplateCatalog
            .FirstOrDefaultAsync(e => e.Id == catalogEntryId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Catalog entry not found.");

        // SSRF pre-flight — the client's connect callback pins each hop, but fail
        // fast with a clear reason on an out-of-policy download URL.
        var refusal = await SsrfGuard
            .ValidateOutboundUrlAsync(entry.DownloadUrl, ssrfOptions.Value.StepCatalog, ct)
            .ConfigureAwait(false);
        if (refusal is not null)
        {
            throw new InvalidOperationException(
                $"Refusing to download step template '{entry.Name}': {refusal}");
        }

        var http = httpClientFactory.CreateClient(HttpClientName);
        var json = await http.GetStringAsync(entry.DownloadUrl, ct).ConfigureAwait(false);

        return await stepTemplateService.ImportFromJsonAsync(
            json,
            importSource: $"github.com/{Owner}/{Repo} ({entry.PathInRepo})",
            source: StepTemplateSource.CommunityLibrary,
            ct: ct).ConfigureAwait(false);
    }

    // ── Refresh ────────────────────────────────────────────────────────────

    /// <summary>
    /// Refreshes the catalog from GitHub. Returns a summary describing how many
    /// entries were added / updated / unchanged / removed. Safe to call as
    /// often as you like — only changed file SHAs trigger a per-file fetch.
    /// </summary>
    public async Task<CatalogRefreshResult> RefreshAsync(CancellationToken ct = default)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);

        // 1. Tree listing — single API call.
        var treeUrl = $"https://api.github.com/repos/{Owner}/{Repo}/git/trees/{Branch}?recursive=1";
        JsonNode? treeNode;
        try
        {
            treeNode = await http.GetFromJsonAsync<JsonNode>(treeUrl, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Failed to fetch catalog tree from GitHub.");
            throw new InvalidOperationException(
                $"GitHub tree fetch failed: {ex.Message}", ex);
        }

        var tree = treeNode?["tree"]?.AsArray()
            ?? throw new InvalidOperationException("GitHub response missing 'tree' array.");

        // Map of path → file SHA, restricted to step-templates/*.json blobs.
        var upstreamFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in tree.OfType<JsonObject>())
        {
            var type = node["type"]?.GetValue<string>();
            var path = node["path"]?.GetValue<string>();
            var sha  = node["sha"]?.GetValue<string>();
            if (type != "blob" || path is null || sha is null) { continue; }
            if (!path.StartsWith(SubDir + "/", StringComparison.OrdinalIgnoreCase)) { continue; }
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) { continue; }
            upstreamFiles[path] = sha;
        }

        // 2. Compare against what we have locally.
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.StepTemplateCatalog
            .ToDictionaryAsync(e => e.PathInRepo, StringComparer.OrdinalIgnoreCase, ct)
            .ConfigureAwait(false);

        var added     = 0;
        var updated   = 0;
        var unchanged = 0;
        var removed   = 0;
        var failed    = 0;
        var now = DateTimeOffset.UtcNow;

        // 3. Per-changed-file fetch via raw URL (doesn't count against API limit).
        foreach (var (path, sha) in upstreamFiles)
        {
            ct.ThrowIfCancellationRequested();

            var hadExisting = existing.TryGetValue(path, out var row);
            if (hadExisting && row!.FileSha == sha)
            {
                unchanged++;
                row.LastSyncedUtc = now;
                continue;
            }

            var rawUrl = $"https://raw.githubusercontent.com/{Owner}/{Repo}/{Branch}/{path}";
            string fileJson;
            try
            {
                fileJson = await http.GetStringAsync(rawUrl, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to fetch catalog file {Path}.", path);
                failed++;
                continue;
            }

            StepTemplateCatalogEntryMetadata meta;
            try
            {
                meta = ParseMetadata(fileJson);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to parse catalog file {Path}.", path);
                failed++;
                continue;
            }

            if (hadExisting)
            {
                row!.FileSha             = sha;
                row.DownloadUrl          = rawUrl;
                row.Name                 = meta.Name;
                row.ActionType           = meta.ActionType;
                row.Description          = meta.Description;
                row.Category             = meta.Category;
                row.Author               = meta.Author;
                row.Website              = meta.Website;
                row.LogoUrl              = meta.LogoUrl;
                row.Version              = meta.Version;
                row.CommunityTemplateId  = meta.CommunityTemplateId ?? row.CommunityTemplateId;
                row.LastSyncedUtc        = now;
                updated++;
            }
            else
            {
                if (meta.CommunityTemplateId is null)
                {
                    // Library entries always carry an Id; if missing, skip.
                    failed++;
                    continue;
                }

                db.StepTemplateCatalog.Add(new StepTemplateCatalogEntry
                {
                    CommunityTemplateId = meta.CommunityTemplateId,
                    PathInRepo          = path,
                    FileSha             = sha,
                    DownloadUrl         = rawUrl,
                    Name                = meta.Name,
                    ActionType          = meta.ActionType,
                    Description         = meta.Description,
                    Category            = meta.Category,
                    Author              = meta.Author,
                    Website             = meta.Website,
                    LogoUrl             = meta.LogoUrl,
                    Version             = meta.Version,
                    LastSyncedUtc       = now,
                });
                added++;
            }
        }

        // 4. Delete orphans (files removed upstream).
        foreach (var (path, row) in existing)
        {
            if (!upstreamFiles.ContainsKey(path))
            {
                db.StepTemplateCatalog.Remove(row);
                removed++;
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var result = new CatalogRefreshResult(
            UpstreamCount: upstreamFiles.Count,
            Added:         added,
            Updated:       updated,
            Unchanged:     unchanged,
            Removed:       removed,
            Failed:        failed);

        logger.LogInformation(
            "Catalog refresh: upstream={Upstream} added={Added} updated={Updated} " +
            "unchanged={Unchanged} removed={Removed} failed={Failed}.",
            result.UpstreamCount, result.Added, result.Updated,
            result.Unchanged, result.Removed, result.Failed);

        return result;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static StepTemplateCatalogEntryMetadata ParseMetadata(string json)
    {
        var root = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException("Catalog JSON root is not an object.");

        var name = root["Name"]?.GetValue<string>()?.Trim()
            ?? throw new InvalidOperationException("'Name' is required.");
        var actionType = root["ActionType"]?.GetValue<string>()?.Trim()
            ?? throw new InvalidOperationException("'ActionType' is required.");

        return new StepTemplateCatalogEntryMetadata(
            CommunityTemplateId: (root["CommunityActionTemplateId"] ?? root["Id"])
                                 ?.GetValue<string>()?.Trim(),
            Name:                name,
            ActionType:          actionType,
            Description:         root["Description"]?.GetValue<string>()?.Trim(),
            Category:            root["Category"]?.GetValue<string>()?.Trim(),
            Author:              root["Author"]?.GetValue<string>()?.Trim(),
            Website:             (root["Website"] ?? root["WebsiteUrl"])
                                 ?.GetValue<string>()?.Trim(),
            LogoUrl:             (root["LogoUrl"] ?? root["Logo"])
                                 ?.GetValue<string>()?.Trim(),
            Version:             root["Version"]?.GetValue<int>() ?? 1);
    }

    private sealed record StepTemplateCatalogEntryMetadata(
        string? CommunityTemplateId,
        string Name,
        string ActionType,
        string? Description,
        string? Category,
        string? Author,
        string? Website,
        string? LogoUrl,
        int Version);
}

/// <summary>Summary returned by <see cref="StepTemplateCatalogService.RefreshAsync"/>.</summary>
public sealed record CatalogRefreshResult(
    int UpstreamCount,
    int Added,
    int Updated,
    int Unchanged,
    int Removed,
    int Failed);
