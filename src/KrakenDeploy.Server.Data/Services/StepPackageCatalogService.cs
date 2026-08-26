using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KrakenDeploy.Contracts.StepPackages;
using KrakenDeploy.Server.Core.Domain.StepPackages;
using KrakenDeploy.Server.Data.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Caches step-package metadata from the Kraken community repo's GitHub
/// Releases feed in the local <c>step_package_catalog</c> table (Phase D-9,
/// default repo corrected in SC6 — the old <c>KrakenDeploy/StepPackages</c>
/// default pointed at a squatted GitHub name and 404'd on every poll).
/// Used by the catalog tab on <c>/step-packages</c>. Refreshed hourly by a
/// Hangfire recurring job and on demand via the "Refresh" button.
/// <para>
/// Strategy:
/// <list type="number">
///   <item>One GitHub API call to <c>GET /repos/{owner}/{repo}/releases</c>
///         returns every Release with its assets + body. Single request,
///         cheap on the 60-req/hour unauthenticated rate budget.</item>
///   <item>For each Release: find the <c>.kdeploy-step</c> asset (skip if
///         none). Extract the manifest from the release notes — the
///         publisher embeds it as a fenced <c>```json</c> block — and
///         persist <see cref="StepPackageCatalogEntry"/> rows keyed by
///         (manifest.id, manifest.version). Asset downloads happen only at
///         install time, not on every refresh.</item>
///   <item>Orphan releases (deleted upstream) get cleared from the
///         catalog table on the next sync.</item>
/// </list>
/// </para>
/// <para>
/// Configuration keys (under <c>StepPackages:Catalog</c>):
/// <list type="bullet">
///   <item><c>Owner</c> — GitHub owner. Default <c>"DomagojJugovich"</c>.</item>
///   <item><c>Repo</c> — Repo name. Default <c>"kraken-steps"</c>.</item>
///   <item><c>Enabled</c> — When <c>false</c>, refresh is a no-op (useful
///         for air-gapped servers). Default <c>true</c>.</item>
/// </list>
/// A <c>GitHub:Token</c> setting (shared with
/// <see cref="StepTemplateCatalogService"/>) lifts the rate limit if set —
/// the named HttpClient applies it as a bearer token in <c>Program.cs</c>.
/// </para>
/// </summary>
public class StepPackageCatalogService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    IHttpClientFactory httpClientFactory,
    StepPackageService stepPackageService,
    EffectiveSettingsService effectiveSettings,
    IOptions<SsrfOptions> ssrfOptions,
    ILogger<StepPackageCatalogService> logger,
    SettingsService? settings = null)
{
    /// <summary>Named <see cref="HttpClient"/> shared with the step-template catalog.</summary>
    public const string HttpClientName = "kraken.github";

    // ── Queries ────────────────────────────────────────────────────────────

    public async Task<List<StepPackageCatalogEntry>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.StepPackageCatalog
            .AsNoTracking()
            .OrderBy(e => e.Name)
            .ThenByDescending(e => e.PublishedUtc)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<DateTimeOffset?> GetLastSyncAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (!await db.StepPackageCatalog.AnyAsync(ct).ConfigureAwait(false))
        {
            return null;
        }
        return await db.StepPackageCatalog
            .MaxAsync(e => (DateTimeOffset?)e.LastSyncedUtc, ct).ConfigureAwait(false);
    }

    // ── Refresh ────────────────────────────────────────────────────────────

    /// <summary>
    /// Refreshes the catalog from GitHub Releases. Safe to call on demand
    /// and from the hourly recurring job. When <c>StepPackages:Catalog:Enabled</c>
    /// is false, returns an empty <see cref="CatalogRefreshResult"/> immediately.
    /// </summary>
    public async Task<CatalogRefreshResult> RefreshAsync(CancellationToken ct = default)
    {
        var catalog = await effectiveSettings.GetCatalogAsync(ct).ConfigureAwait(false);
        var owner = catalog.PackageCatalogOwner.Value;
        var repo = catalog.PackageCatalogRepo.Value;
        var healthKey = $"packages:{owner}/{repo}".ToLowerInvariant();

        if (!catalog.PackageCatalogEnabled.Value)
        {
            logger.LogDebug("Step-package catalog refresh skipped — disabled by effective settings.");
            return new CatalogRefreshResult(0, 0, 0, 0, 0, 0);
        }

        var http = httpClientFactory.CreateClient(HttpClientName);
        var releasesUrl = $"https://api.github.com/repos/{owner}/{repo}/releases?per_page=100";
        await GitHubHttpClientAuthentication.ApplyAsync(
                http, effectiveSettings, new Uri(releasesUrl), ct)
            .ConfigureAwait(false);

        JsonNode? releasesNode;
        try
        {
            releasesNode = await http.GetFromJsonAsync<JsonNode>(releasesUrl, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex,
                "Failed to fetch step-package catalog from GitHub ({Owner}/{Repo}).", owner, repo);
            await RecordFeedHealthAsync(healthKey, $"GitHub releases fetch failed: {ex.Message}", ct)
                .ConfigureAwait(false);
            throw new InvalidOperationException(
                $"GitHub releases fetch failed for {owner}/{repo}: {ex.Message}", ex);
        }

        var releases = releasesNode?.AsArray()
            ?? throw new InvalidOperationException("GitHub response is not a releases array.");

        // Build (name, version) → discovered entry from upstream so we can
        // diff against the existing catalog table.
        var upstreamByKey = new Dictionary<(string Name, string Version), DiscoveredRelease>();
        var failed       = 0;

        foreach (var node in releases.OfType<JsonObject>())
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var discovered = DiscoverFromRelease(node);
                if (discovered is null) { continue; } // not a step-package release
                upstreamByKey[(discovered.Manifest.Id, discovered.Manifest.Version)] = discovered;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to parse release {Tag}.",
                    node["tag_name"]?.GetValue<string>() ?? "<unknown>");
                failed++;
            }
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.StepPackageCatalog
            .ToDictionaryAsync(e => (e.Name, e.Version), ct).ConfigureAwait(false);

        var added     = 0;
        var updated   = 0;
        var unchanged = 0;
        var removed   = 0;
        var now       = DateTimeOffset.UtcNow;

        foreach (var (key, discovered) in upstreamByKey)
        {
            if (existing.TryGetValue(key, out var row))
            {
                if (row.Sha256 == discovered.Sha256
                    && row.DownloadUrl == discovered.DownloadUrl)
                {
                    unchanged++;
                    row.LastSyncedUtc = now;
                    continue;
                }

                row.DownloadUrl    = discovered.DownloadUrl;
                row.Sha256         = discovered.Sha256;
                row.ManifestJson   = discovered.RawManifestJson;
                row.Changelog      = discovered.Changelog;
                row.PublishedUtc   = discovered.PublishedUtc;
                row.ReleaseHtmlUrl = discovered.ReleaseHtmlUrl;
                row.LastSyncedUtc  = now;
                updated++;
            }
            else
            {
                db.StepPackageCatalog.Add(new StepPackageCatalogEntry
                {
                    Name           = discovered.Manifest.Id,
                    Version        = discovered.Manifest.Version,
                    DownloadUrl    = discovered.DownloadUrl,
                    Sha256         = discovered.Sha256,
                    ManifestJson   = discovered.RawManifestJson,
                    Changelog      = discovered.Changelog,
                    PublishedUtc   = discovered.PublishedUtc,
                    ReleaseHtmlUrl = discovered.ReleaseHtmlUrl,
                    LastSyncedUtc  = now,
                });
                added++;
            }
        }

        foreach (var (key, row) in existing)
        {
            if (!upstreamByKey.ContainsKey(key))
            {
                db.StepPackageCatalog.Remove(row);
                removed++;
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var result = new CatalogRefreshResult(
            UpstreamCount: upstreamByKey.Count,
            Added:         added,
            Updated:       updated,
            Unchanged:     unchanged,
            Removed:       removed,
            Failed:        failed);

        logger.LogInformation(
            "Step-package catalog refresh ({Owner}/{Repo}): upstream={Upstream} added={Added} " +
            "updated={Updated} unchanged={Unchanged} removed={Removed} failed={Failed}.",
            owner, repo, result.UpstreamCount, result.Added, result.Updated,
            result.Unchanged, result.Removed, result.Failed);

        await RecordFeedHealthAsync(healthKey, error: null, ct).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// SC6: records this feed's last attempt / success / error in the
    /// <see cref="StepFeedHealthDocument"/>, surfaced by the picker's
    /// feed-health strip and the catalog tab. Best-effort — never fails
    /// the refresh itself.
    /// </summary>
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

    // ── Install ────────────────────────────────────────────────────────────

    /// <summary>
    /// Downloads a catalogued <c>.kdeploy-step</c> archive, verifies its
    /// SHA-256 against the catalog row, and installs it via the same path
    /// as a manual upload (<see cref="StepPackageService.UploadAsync"/>)
    /// with <see cref="StepPackageSource.CatalogPull"/>. Throws on hash
    /// mismatch or install failure.
    /// </summary>
    public async Task<StepPackage> InstallAsync(
        string name, string version, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entry = await db.StepPackageCatalog
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Name == name && e.Version == version, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Catalog entry for {name} {version} not found. " +
                "Refresh the catalog and try again.");

        // SSRF pre-flight — entry.DownloadUrl comes from upstream release JSON
        // (browser_download_url) and its host is NOT constrained to github.com.
        // The client's connect callback re-validates + pins each hop too, but a
        // pre-flight check fails fast with a clear reason.
        var refusal = await SsrfGuard
            .ValidateOutboundUrlAsync(entry.DownloadUrl, ssrfOptions.Value.StepCatalog, ct)
            .ConfigureAwait(false);
        if (refusal is not null)
        {
            throw new InvalidOperationException(
                $"Refusing to download {name} {version}: {refusal}");
        }

        var http = httpClientFactory.CreateClient(HttpClientName);
        await GitHubHttpClientAuthentication.ApplyAsync(
                http, effectiveSettings, new Uri(entry.DownloadUrl), ct)
            .ConfigureAwait(false);

        // GitHub release assets need a specific Accept header to return the
        // raw binary (without it we get the JSON metadata).
        using var request = new HttpRequestMessage(HttpMethod.Get, entry.DownloadUrl);
        request.Headers.Accept.ParseAdd("application/octet-stream");

        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // Stream the body to a temp file so we can hash + re-stream into the
        // upload path without loading the whole archive into memory.
        var tempPath = Path.Combine(Path.GetTempPath(),
            $"kraken-catalog-{Guid.NewGuid():N}.kdeploy-step");
        try
        {
            string downloadedSha;
            await using (var responseStream = await response.Content
                .ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var fs = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true))
            {
                await responseStream.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            await using (var hashStream = File.OpenRead(tempPath))
            {
                downloadedSha = Convert.ToHexStringLower(
                    await SHA256.HashDataAsync(hashStream, ct).ConfigureAwait(false));
            }

            if (!string.Equals(downloadedSha, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"SHA-256 mismatch for {name} {version}: catalog says {entry.Sha256}, " +
                    $"downloaded {downloadedSha}. Refusing install.");
            }

            await using var installStream = File.OpenRead(tempPath);
            var result = await stepPackageService
                .UploadAsync(installStream, StepPackageSource.CatalogPull, ct)
                .ConfigureAwait(false);

            if (!result.Success)
            {
                throw new InvalidOperationException(
                    $"Catalog install failed for {name} {version}: {result.ErrorMessage}");
            }

            return result.Installed!;
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* best effort */ }
        }
    }

    // ── Discovery helpers ──────────────────────────────────────────────────

    private sealed record DiscoveredRelease(
        StepPackageManifest Manifest,
        string RawManifestJson,
        string DownloadUrl,
        string Sha256,
        string? Changelog,
        DateTimeOffset PublishedUtc,
        string ReleaseHtmlUrl);

    /// <summary>
    /// Discovers a step-package release from a GitHub <c>release</c> object:
    /// finds the <c>.kdeploy-step</c> asset, extracts the manifest from the
    /// release notes' fenced JSON block, returns <c>null</c> if either is
    /// missing (not every release in the repo has to be a step package).
    /// </summary>
    private static DiscoveredRelease? DiscoverFromRelease(JsonObject release)
    {
        if (release["draft"]?.GetValue<bool>() == true)        { return null; }
        if (release["prerelease"]?.GetValue<bool>() == true)   { return null; }

        var assets = release["assets"]?.AsArray();
        if (assets is null) { return null; }

        // Find the .kdeploy-step asset.
        JsonObject? asset = null;
        foreach (var a in assets.OfType<JsonObject>())
        {
            var n = a["name"]?.GetValue<string>();
            if (n is not null && n.EndsWith(".kdeploy-step", StringComparison.OrdinalIgnoreCase))
            {
                asset = a;
                break;
            }
        }
        if (asset is null) { return null; }

        var downloadUrl = asset["browser_download_url"]?.GetValue<string>()
                          ?? throw new InvalidOperationException("Asset missing browser_download_url.");

        // Parse the manifest from the release notes.
        var body = release["body"]?.GetValue<string>() ?? "";
        var (manifest, rawJson) = ExtractManifestFromBody(body);

        // The publisher should embed the SHA-256 in the release notes too.
        // Convention: a separate fenced block tagged "sha256" OR a one-line
        // "SHA-256: <hex>" entry. Fall back to the asset's size-only metadata
        // if missing — the install path will catch a hash mismatch.
        var sha256 = ExtractSha256FromBody(body)
                     ?? throw new InvalidOperationException(
                         "Release notes are missing a sha256 entry. Add a 'SHA-256: <hex>' line " +
                         "or a ```sha256 fenced block so the install can verify the download.");

        var publishedRaw = release["published_at"]?.GetValue<string>()
                           ?? release["created_at"]?.GetValue<string>()
                           ?? throw new InvalidOperationException("Release missing published_at.");
        var publishedUtc = DateTimeOffset.Parse(publishedRaw, System.Globalization.CultureInfo.InvariantCulture);

        var htmlUrl = release["html_url"]?.GetValue<string>() ?? downloadUrl;

        return new DiscoveredRelease(
            Manifest:       manifest,
            RawManifestJson: rawJson,
            DownloadUrl:    downloadUrl,
            Sha256:         sha256.ToLowerInvariant(),
            Changelog:      ExtractChangelogFromBody(body),
            PublishedUtc:   publishedUtc.ToUniversalTime(),
            ReleaseHtmlUrl: htmlUrl);
    }

    private static (StepPackageManifest Manifest, string RawJson) ExtractManifestFromBody(string body)
    {
        // The publisher convention is a single ```json fenced block carrying
        // the package manifest JSON. We match the first JSON block whose
        // contents parse as a step-package manifest.
        var fences = FencedJsonRegex.Matches(body);
        foreach (Match m in fences)
        {
            var json = m.Groups["json"].Value.Trim();
            try
            {
                var manifest = StepPackageManifestJson.Deserialize(json);
                return (manifest, json);
            }
            catch
            {
                // Not the manifest block — keep looking.
                continue;
            }
        }
        throw new InvalidOperationException(
            "Release notes don't contain a parseable ```json manifest block.");
    }

    private static string? ExtractSha256FromBody(string body)
    {
        // Convention 1: ```sha256 ... ``` fenced block.
        var fenceMatch = ShaFenceRegex.Match(body);
        if (fenceMatch.Success)
        {
            return fenceMatch.Groups["hex"].Value.Trim().ToLowerInvariant();
        }

        // Convention 2: "SHA-256: <64 hex>" inline.
        var lineMatch = ShaLineRegex.Match(body);
        return lineMatch.Success ? lineMatch.Groups["hex"].Value.ToLowerInvariant() : null;
    }

    private static string? ExtractChangelogFromBody(string body)
    {
        // Everything in the release notes that ISN'T the JSON manifest block
        // or the sha256 directive — the human-readable changelog. Trim
        // empty leading/trailing lines.
        var stripped = FencedJsonRegex.Replace(body, string.Empty);
        stripped = ShaFenceRegex.Replace(stripped, string.Empty);
        stripped = ShaLineRegex.Replace(stripped, string.Empty);
        stripped = stripped.Trim();
        return stripped.Length == 0 ? null : stripped;
    }

    private static readonly Regex FencedJsonRegex = new(
        @"```json\s*(?<json>[\s\S]*?)\s*```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ShaFenceRegex = new(
        @"```sha256\s*(?<hex>[0-9a-fA-F]{64})\s*```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ShaLineRegex = new(
        @"SHA-?256\s*:\s*(?<hex>[0-9a-fA-F]{64})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
}
