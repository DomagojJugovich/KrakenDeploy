using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using KrakenDeploy.Server.Core.Domain.Settings;
using KrakenDeploy.Server.Core.Domain.StepPackages;
using KrakenDeploy.Server.Core.Domain.StepTemplates;
using KrakenDeploy.Server.Data.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Caches step-template metadata from GitHub-hosted template feeds in the
/// local <c>step_template_catalog</c> table. Used by the
/// <c>/step-templates/community</c> catalog browser and the Add-Step picker's
/// "Available to install" section. Refreshed hourly by a Hangfire recurring
/// job and on demand via the "Refresh" button.
/// <para>
/// SC6: the catalog is MULTI-FEED. Feeds come from configuration
/// (<c>StepTemplates:Catalog:Feeds</c> — array of Owner/Repo/Branch/SubDir),
/// defaulting to the Octopus community library plus the Kraken community
/// repo's <c>step-templates/</c> lane. <c>StepTemplates:Catalog:Enabled</c> =
/// <c>false</c> turns refresh into a no-op (air-gapped installs), matching
/// the step-package catalog's switch.
/// </para>
/// <para>
/// Per-feed strategy (unchanged from the single-feed original):
/// <list type="number">
///   <item>One GitHub API call to the Git Trees endpoint returns every blob's
///         path + SHA in the branch (single request, cheap on the
///         60-req/hr unauthenticated rate limit).</item>
///   <item>For each <c>{subdir}/*.json</c> path whose SHA has changed since
///         the last sync, fetch the raw file via
///         <c>raw.githubusercontent.com</c> (does NOT count against the API
///         rate limit) and extract metadata.</item>
///   <item>Upsert by (feed, path); delete rows whose path no longer appears
///         in THAT feed's tree. One feed's outage never orphan-deletes
///         another feed's rows — and never aborts its sync.</item>
/// </list>
/// Every refresh records per-feed health (last attempt / success / error) in
/// the <see cref="StepFeedHealthDocument"/> settings document, surfaced by
/// the picker's feed-health strip.
/// </para>
/// </summary>
public class StepTemplateCatalogService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    IHttpClientFactory httpClientFactory,
    StepTemplateService stepTemplateService,
    EffectiveSettingsService effectiveSettings,
    IOptions<SsrfOptions> ssrfOptions,
    ILogger<StepTemplateCatalogService> logger,
    SettingsService? settings = null)
{
    /// <summary>Named <see cref="HttpClient"/> registered in <c>Program.cs</c>.</summary>
    public const string HttpClientName = "kraken.github";

    /// <summary>One configured template feed.</summary>
    public sealed record Feed(string Owner, string Repo, string Branch, string SubDir)
    {
        /// <summary>Lower-cased <c>owner/repo</c> — the per-row attribution key.</summary>
        public string Key => $"{Owner}/{Repo}".ToLowerInvariant();

        /// <summary>Key in the <see cref="StepFeedHealthDocument"/>.</summary>
        public string HealthKey => $"templates:{Key}";
    }

    /// <summary>
    /// The configured feeds, or the defaults when none are configured:
    /// the Octopus community library (600+ templates) and the Kraken
    /// community repo's <c>step-templates/</c> lane (SD-12/SD-13).
    /// </summary>
    public async Task<IReadOnlyList<Feed>> ResolveFeedsAsync(CancellationToken ct = default)
    {
        var catalog = await effectiveSettings.GetCatalogAsync(ct).ConfigureAwait(false);
        return ResolveFeeds(catalog.TemplateCatalogFeeds.Value);
    }

    private List<Feed> ResolveFeeds(IEnumerable<CatalogFeedSettings> configuredFeeds)
    {
        var feeds = new List<Feed>();
        foreach (var configured in configuredFeeds)
        {
            var owner = configured.Owner;
            var repo  = configured.Repo;
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
            {
                logger.LogWarning(
                    "StepTemplates:Catalog:Feeds entry is missing Owner/Repo — skipped.");
                continue;
            }
            feeds.Add(new Feed(
                owner.Trim(), repo.Trim(),
                configured.Branch?.Trim() is { Length: > 0 } b ? b : "main",
                configured.SubDir?.Trim() is { Length: > 0 } s ? s : "step-templates"));
        }

        if (feeds.Count == 0)
        {
            feeds =
            [
                new Feed("OctopusDeploy", "Library", "master", "step-templates"),
                new Feed("DomagojJugovich", "kraken-steps", "main", "step-templates"),
            ];
        }
        return feeds;
    }

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
        await GitHubHttpClientAuthentication.ApplyAsync(
                http, effectiveSettings, new Uri(entry.DownloadUrl), ct)
            .ConfigureAwait(false);
        var json = await http.GetStringAsync(entry.DownloadUrl, ct).ConfigureAwait(false);

        return await stepTemplateService.ImportFromJsonAsync(
            json,
            importSource: $"github.com/{entry.FeedKey} ({entry.PathInRepo})",
            source: StepTemplateSource.CommunityLibrary,
            ct: ct).ConfigureAwait(false);
    }

    // ── Refresh ────────────────────────────────────────────────────────────

    /// <summary>
    /// Refreshes the catalog from every configured feed. One feed's failure
    /// is recorded in its health entry and does not abort the others; the
    /// call throws only when EVERY feed failed (so the UI's manual Refresh
    /// still surfaces a total outage). Returns the aggregate summary.
    /// </summary>
    public async Task<CatalogRefreshResult> RefreshAsync(CancellationToken ct = default)
    {
        var catalog = await effectiveSettings.GetCatalogAsync(ct).ConfigureAwait(false);
        if (!catalog.TemplateCatalogEnabled.Value)
        {
            logger.LogDebug("Step-template catalog refresh skipped — disabled by effective settings.");
            return new CatalogRefreshResult(0, 0, 0, 0, 0, 0);
        }

        var feeds = ResolveFeeds(catalog.TemplateCatalogFeeds.Value);
        var totals = new CatalogRefreshResult(0, 0, 0, 0, 0, 0);
        var errors = new List<string>();

        foreach (var feed in feeds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await RefreshFeedAsync(feed, ct).ConfigureAwait(false);
                totals = new CatalogRefreshResult(
                    totals.UpstreamCount + result.UpstreamCount,
                    totals.Added        + result.Added,
                    totals.Updated      + result.Updated,
                    totals.Unchanged    + result.Unchanged,
                    totals.Removed      + result.Removed,
                    totals.Failed       + result.Failed);
                await RecordFeedHealthAsync(feed.HealthKey, error: null, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Step-template feed {Feed} refresh failed; other feeds continue.", feed.Key);
                errors.Add($"{feed.Key}: {ex.Message}");
                await RecordFeedHealthAsync(feed.HealthKey, ex.Message, ct).ConfigureAwait(false);
            }
        }

        if (errors.Count == feeds.Count && feeds.Count > 0)
        {
            throw new InvalidOperationException(
                "Every step-template feed failed to refresh: " + string.Join(" | ", errors));
        }

        logger.LogInformation(
            "Catalog refresh ({Feeds} feed(s)): upstream={Upstream} added={Added} updated={Updated} " +
            "unchanged={Unchanged} removed={Removed} failed={Failed}.",
            feeds.Count, totals.UpstreamCount, totals.Added, totals.Updated,
            totals.Unchanged, totals.Removed, totals.Failed);

        return totals;
    }

    private async Task<CatalogRefreshResult> RefreshFeedAsync(Feed feed, CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);

        // 1. Tree listing — single API call.
        var treeUrl =
            $"https://api.github.com/repos/{feed.Owner}/{feed.Repo}/git/trees/{feed.Branch}?recursive=1";
        await GitHubHttpClientAuthentication.ApplyAsync(
                http, effectiveSettings, new Uri(treeUrl), ct)
            .ConfigureAwait(false);
        JsonNode? treeNode;
        try
        {
            treeNode = await http.GetFromJsonAsync<JsonNode>(treeUrl, ct).ConfigureAwait(false);
            http.DefaultRequestHeaders.Authorization = null;
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"GitHub tree fetch failed for {feed.Key}: {ex.Message}", ex);
        }

        var tree = treeNode?["tree"]?.AsArray()
            ?? throw new InvalidOperationException("GitHub response missing 'tree' array.");

        // Map of path → file SHA, restricted to {subdir}/*.json blobs.
        var upstreamFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in tree.OfType<JsonObject>())
        {
            var type = node["type"]?.GetValue<string>();
            var path = node["path"]?.GetValue<string>();
            var sha  = node["sha"]?.GetValue<string>();
            if (type != "blob" || path is null || sha is null) { continue; }
            if (!path.StartsWith(feed.SubDir + "/", StringComparison.OrdinalIgnoreCase)) { continue; }
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) { continue; }
            upstreamFiles[path] = sha;
        }

        // 2. Compare against what we have locally — for THIS feed only.
        // Scoped by SubDir as well as FeedKey: Feed.Key is owner/repo, so two
        // configured feeds over the same repo but different subdirectories share
        // a key, and an unscoped orphan sweep would have each one delete all of
        // the other's rows on every refresh.
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        // Case-insensitive prefix match: the upstream tree filter (line ~263) and
        // both dictionaries are OrdinalIgnoreCase, so a plain StartsWith here
        // (a case-SENSITIVE Postgres LIKE) would load an empty `existing` set when
        // the repo's real subdir casing differs from feed.SubDir, treating every
        // file as new and freezing the feed on the CommunityTemplateId unique index.
        // ILike is Npgsql's case-insensitive LIKE; escape LIKE metacharacters in
        // the (config-supplied) subdir so '_'/'%' in a dir name stay literal.
        var subDirLike = (feed.SubDir + "/")
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal) + "%";
        var existing = await db.StepTemplateCatalog
            .Where(e => e.FeedKey == feed.Key && EF.Functions.ILike(e.PathInRepo, subDirLike))
            .ToDictionaryAsync(e => e.PathInRepo, StringComparer.OrdinalIgnoreCase, ct)
            .ConfigureAwait(false);

        var added     = 0;
        var updated   = 0;
        var unchanged = 0;
        var removed   = 0;
        var failed    = 0;
        var now = DateTimeOffset.UtcNow;

        // 2b. Delete orphans FIRST, in their own SaveChanges. CommunityTemplateId
        // is globally unique, so a template that merely MOVED path upstream would
        // otherwise be rejected as a duplicate of the row that is about to be
        // deleted, and the entry would vanish for a whole refresh cycle. EF orders
        // inserts before deletes within one SaveChanges, so the two must be split.
        foreach (var (path, row) in existing)
        {
            if (!upstreamFiles.ContainsKey(path))
            {
                db.StepTemplateCatalog.Remove(row);
                removed++;
            }
        }
        if (removed > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            foreach (var path in existing.Keys.Where(p => !upstreamFiles.ContainsKey(p)).ToList())
            {
                existing.Remove(path);
            }
        }

        // CommunityTemplateId is globally unique — a duplicate id arriving
        // from a second feed must be skipped (counted failed), not blow up
        // the whole feed's SaveChanges. Loaded after the orphan delete so ids
        // freed by it are available to the adds below.
        var knownIds = (await db.StepTemplateCatalog
                .Select(e => e.CommunityTemplateId)
                .ToListAsync(ct).ConfigureAwait(false))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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

            var rawUrl =
                $"https://raw.githubusercontent.com/{feed.Owner}/{feed.Repo}/{feed.Branch}/{path}";
            string fileJson;
            try
            {
                fileJson = await http.GetStringAsync(rawUrl, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to fetch catalog file {Feed}/{Path}.", feed.Key, path);
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
                logger.LogWarning(ex, "Failed to parse catalog file {Feed}/{Path}.", feed.Key, path);
                failed++;
                continue;
            }

            if (hadExisting)
            {
                // A changed id must clear the same global-uniqueness bar the add
                // branch enforces. Letting it through means SaveChanges throws on
                // ix_step_template_catalog_community_template_id, which rolls back
                // the WHOLE feed pass (adds, sync bumps, orphan deletes) and
                // re-throws every hour because nothing was persisted.
                if (meta.CommunityTemplateId is not null
                    && !string.Equals(meta.CommunityTemplateId, row!.CommunityTemplateId,
                                      StringComparison.OrdinalIgnoreCase))
                {
                    if (!knownIds.Add(meta.CommunityTemplateId))
                    {
                        logger.LogWarning(
                            "Catalog file {Feed}/{Path} changed its CommunityTemplateId to '{Id}', " +
                            "which another row already holds — row left unchanged.",
                            feed.Key, path, meta.CommunityTemplateId);
                        failed++;
                        continue;
                    }
                    knownIds.Remove(row.CommunityTemplateId);
                }

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
                if (!knownIds.Add(meta.CommunityTemplateId))
                {
                    logger.LogWarning(
                        "Catalog file {Feed}/{Path} carries CommunityTemplateId '{Id}' that another " +
                        "feed already provides — skipped.", feed.Key, path, meta.CommunityTemplateId);
                    failed++;
                    continue;
                }

                db.StepTemplateCatalog.Add(new StepTemplateCatalogEntry
                {
                    CommunityTemplateId = meta.CommunityTemplateId,
                    FeedKey             = feed.Key,
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

        // 4. Orphans were already deleted in step 2b (before the adds, so a moved
        // template's id is free by the time its new path is inserted).
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new CatalogRefreshResult(
            UpstreamCount: upstreamFiles.Count,
            Added:         added,
            Updated:       updated,
            Unchanged:     unchanged,
            Removed:       removed,
            Failed:        failed);
    }

    private async Task RecordFeedHealthAsync(string healthKey, string? error, CancellationToken ct)
    {
        if (settings is null) { return; }
        try
        {
            var now = DateTimeOffset.UtcNow;
            await settings.MutateAsync<StepFeedHealthDocument>(null, doc =>
            {
                doc.Feeds.TryGetValue(healthKey, out var prev);
                doc.Feeds[healthKey] = new StepFeedHealth
                {
                    LastAttemptUtc = now,
                    LastSuccessUtc = error is null ? now : prev?.LastSuccessUtc,
                    LastError      = error,
                };
                return doc;
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to record feed health for {Feed}.", healthKey);
        }
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
