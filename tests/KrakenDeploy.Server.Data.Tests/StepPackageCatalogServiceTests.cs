using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.StepPackages;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Integration tests for the Phase D-9 step-package catalog. The HTTP
/// surface against GitHub is exercised end-to-end via a stubbed
/// <see cref="HttpClient"/> handler so the tests don't depend on a live
/// internet connection or an actual repo existing. The interesting logic
/// — release-notes manifest extraction, SHA-256 directive parsing,
/// upsert + orphan cleanup, idempotent refresh — is all server-side.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class StepPackageCatalogServiceTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task RefreshAsync_persists_a_release_with_an_embedded_manifest_block()
    {
        var pkgName = UniquePackageName();
        var stubbed = StubGitHubReleases([
            BuildReleaseJson(pkgName, "1.0.0",
                "Some changelog goes here.\n\n" +
                "```json\n" +
                $"{{\"id\":\"{pkgName}\",\"version\":\"1.0.0\",\"displayName\":\"Sample\"," +
                "\"targetFramework\":\"net10.0\",\"stepTypes\":[\"X\"]," +
                "\"executorAssembly\":\"X.dll\",\"executorTypeName\":\"X.Y\"," +
                "\"signedBy\":\"k\",\"signature\":\"unsigned-dev-build\"}\n" +
                "```\n\n" +
                "SHA-256: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
        ]);

        var svc    = NewSvc(stubbed);
        var result = await svc.RefreshAsync();

        result.Added.Should().Be(1);
        result.UpstreamCount.Should().Be(1);
        result.Failed.Should().Be(0);

        await using var db = postgres.CreateContext();
        var row = await db.StepPackageCatalog
            .FirstOrDefaultAsync(e => e.Name == pkgName && e.Version == "1.0.0");
        row.Should().NotBeNull();
        row!.Sha256.Should().Be("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
        row.Changelog.Should().Contain("Some changelog");
        row.DownloadUrl.Should().EndWith($"{pkgName}-1.0.0.kdeploy-step");
    }

    [Fact]
    public async Task RefreshAsync_skips_draft_and_prerelease_entries()
    {
        var pkgName = UniquePackageName();
        var stubbed = StubGitHubReleases([
            BuildReleaseJson(pkgName, "1.0.0", BuildValidBody(pkgName, "1.0.0"), draft: true),
            BuildReleaseJson(pkgName, "1.1.0-rc.1", BuildValidBody(pkgName, "1.1.0-rc.1"), prerelease: true),
            BuildReleaseJson(pkgName, "1.0.1", BuildValidBody(pkgName, "1.0.1")),
        ]);

        var result = await NewSvc(stubbed).RefreshAsync();

        result.UpstreamCount.Should().Be(1, "drafts + prereleases are silently filtered");
        result.Added.Should().Be(1);

        await using var db = postgres.CreateContext();
        (await db.StepPackageCatalog
            .Where(e => e.Name == pkgName)
            .Select(e => e.Version)
            .ToListAsync())
            .Should().Equal(["1.0.1"]);
    }

    [Fact]
    public async Task RefreshAsync_skips_releases_missing_the_kdeploy_step_asset()
    {
        var pkgName = UniquePackageName();
        var stubbed = StubGitHubReleases([
            BuildReleaseJson(pkgName, "1.0.0", BuildValidBody(pkgName, "1.0.0"),
                assetName: "not-a-step-package.zip"),
        ]);

        var result = await NewSvc(stubbed).RefreshAsync();

        result.UpstreamCount.Should().Be(0,
            "only releases carrying a .kdeploy-step asset count as step-package releases");
        result.Added.Should().Be(0);
    }

    [Fact]
    public async Task RefreshAsync_counts_parse_failures_without_aborting_the_pass()
    {
        var pkgName = UniquePackageName();
        var stubbed = StubGitHubReleases([
            // Bad: no manifest JSON block.
            BuildReleaseJson(pkgName, "1.0.0", "Just some changelog, no JSON block.\n\nSHA-256: " + new string('a', 64)),
            // Good: parseable.
            BuildReleaseJson(pkgName, "1.0.1", BuildValidBody(pkgName, "1.0.1")),
        ]);

        var result = await NewSvc(stubbed).RefreshAsync();

        result.Failed.Should().Be(1, "the body without a JSON manifest block must be counted as failed");
        result.Added.Should().Be(1, "the good release still gets persisted");
    }

    [Fact]
    public async Task RefreshAsync_removes_orphan_rows_after_release_disappears_upstream()
    {
        var pkgName = UniquePackageName();
        var stubbedV1 = StubGitHubReleases([
            BuildReleaseJson(pkgName, "1.0.0", BuildValidBody(pkgName, "1.0.0")),
        ]);
        await NewSvc(stubbedV1).RefreshAsync();

        // Upstream shrinks — the release is gone, but the row still exists.
        var stubbedEmpty = StubGitHubReleases([]);
        var result = await NewSvc(stubbedEmpty).RefreshAsync();

        result.Removed.Should().Be(1);
        await using var db = postgres.CreateContext();
        (await db.StepPackageCatalog.AnyAsync(e => e.Name == pkgName)).Should().BeFalse(
            "an orphaned row gets cleaned up on the next refresh");
    }

    [Fact]
    public async Task RefreshAsync_is_a_noop_when_catalog_is_disabled_by_config()
    {
        var stubbed = StubGitHubReleases([
            BuildReleaseJson("kraken.unused", "1.0.0", BuildValidBody("kraken.unused", "1.0.0")),
        ]);

        var svc = NewSvc(stubbed, extraConfig: new Dictionary<string, string?>
        {
            ["StepPackages:Catalog:Enabled"] = "false",
        });

        var result = await svc.RefreshAsync();
        result.UpstreamCount.Should().Be(0);
        result.Added.Should().Be(0);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private StepPackageCatalogService NewSvc(
        HttpMessageHandler handler,
        Dictionary<string, string?>? extraConfig = null)
    {
        var configValues = new Dictionary<string, string?>
        {
            ["DataPath"]                          = Path.Combine(Path.GetTempPath(),
                $"kraken-catalog-test-{Guid.NewGuid():N}"),
            ["StepPackages:AllowUnsignedUploads"] = "true",
            ["StepPackages:Catalog:Owner"]        = "KrakenDeploy",
            ["StepPackages:Catalog:Repo"]         = "StepPackages",
        };
        if (extraConfig is not null)
        {
            foreach (var (k, v) in extraConfig) { configValues[k] = v; }
        }
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IHttpClientFactory>(
            new StubHttpClientFactory(new HttpClient(handler)));
        services.AddSingleton(config);
        var sp = services.BuildServiceProvider();

        var stepPkgSvc = new StepPackageService(
            postgres, config, NullLogger<StepPackageService>.Instance);

        return new StepPackageCatalogService(
            postgres,
            sp.GetRequiredService<IHttpClientFactory>(),
            stepPkgSvc,
            config,
            Microsoft.Extensions.Options.Options.Create(new Net.SsrfOptions()),
            NullLogger<StepPackageCatalogService>.Instance);
    }

    private static StubReleasesHandler StubGitHubReleases(string[] releaseJsonObjects)
    {
        var array = "[" + string.Join(",", releaseJsonObjects) + "]";
        return new StubReleasesHandler(array);
    }

    private static string BuildValidBody(string name, string version) =>
        "```json\n" +
        $"{{\"id\":\"{name}\",\"version\":\"{version}\",\"displayName\":\"Sample\"," +
        "\"targetFramework\":\"net10.0\",\"stepTypes\":[\"X\"]," +
        "\"executorAssembly\":\"X.dll\",\"executorTypeName\":\"X.Y\"," +
        "\"signedBy\":\"k\",\"signature\":\"unsigned-dev-build\"}\n" +
        "```\n\n" +
        "SHA-256: " + new string('a', 64);

    private static string BuildReleaseJson(
        string name, string version, string body,
        bool draft = false, bool prerelease = false,
        string? assetName = null)
    {
        var asset = assetName ?? $"{name}-{version}.kdeploy-step";
        // Manually-built JSON to avoid the JsonNode-vs-string escape churn
        // of System.Text.Json for a 5-field shape we control.
        var escapedBody = System.Text.Json.JsonSerializer.Serialize(body);
        return $$"""
        {
          "tag_name": "v{{version}}",
          "draft": {{(draft ? "true" : "false")}},
          "prerelease": {{(prerelease ? "true" : "false")}},
          "published_at": "2026-05-20T12:00:00Z",
          "html_url": "https://github.com/KrakenDeploy/StepPackages/releases/tag/{{name}}-v{{version}}",
          "body": {{escapedBody}},
          "assets": [
            {
              "name": "{{asset}}",
              "browser_download_url": "https://github.com/KrakenDeploy/StepPackages/releases/download/v{{version}}/{{name}}-{{version}}.kdeploy-step"
            }
          ]
        }
        """;
    }

    private static string UniquePackageName()
        => "kraken.catalog." + Guid.NewGuid().ToString("N")[..8];

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    /// <summary>
    /// Returns the supplied releases array for any GET that looks like
    /// /releases. Throws for everything else (would be an install
    /// download attempt — none of the refresh tests trigger that path).
    /// </summary>
    private sealed class StubReleasesHandler(string releasesJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.Contains("/releases") == true)
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(releasesJson,
                        System.Text.Encoding.UTF8, "application/json"),
                });
            }

            throw new NotSupportedException(
                $"Unexpected HTTP request in test: {request.Method} {request.RequestUri}.");
        }
    }
}
