using System.Net;
using System.Text;
using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Settings;
using KrakenDeploy.Server.Data.Net;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Data.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// SC6: the step-template catalog is multi-feed. Feeds come from config
/// (defaulting to Octopus's library + the Kraken community repo), rows are
/// attributed to their feed, orphan removal is scoped per feed, and one
/// feed's outage neither aborts nor orphan-deletes the others.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class StepTemplateCatalogMultiFeedTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string MasterKey = "S3Jha2VuRGVwbG95RGV2TWFzdGVyS2V5MzJCeXRlcyE=";

    public Task InitializeAsync() => DeleteCatalogSettingsAsync();
    public Task DisposeAsync() => DeleteCatalogSettingsAsync();

    [Fact]
    public async Task ResolveFeeds_defaults_to_octopus_library_and_kraken_community()
    {
        var svc = NewSvc(new RoutingHandler(), feedsConfig: null);

        var feeds = await svc.ResolveFeedsAsync();

        feeds.Should().HaveCount(2);
        feeds[0].Key.Should().Be("octopusdeploy/library");
        feeds[0].Branch.Should().Be("master");
        feeds[1].Key.Should().Be("domagojjugovich/kraken-steps");
        feeds[1].Branch.Should().Be("main");
        feeds.Should().OnlyContain(f => f.SubDir == "step-templates");
    }

    [Fact]
    public async Task ResolveFeeds_reads_configured_feeds_and_skips_incomplete_entries()
    {
        var svc = NewSvc(new RoutingHandler(), feedsConfig: new Dictionary<string, string?>
        {
            ["StepTemplates:Catalog:Feeds:0:Owner"]  = "acme",
            ["StepTemplates:Catalog:Feeds:0:Repo"]   = "steps",
            ["StepTemplates:Catalog:Feeds:0:Branch"] = "develop",
            ["StepTemplates:Catalog:Feeds:1:Owner"]  = "broken-no-repo",
        });

        var feeds = await svc.ResolveFeedsAsync();

        feeds.Should().ContainSingle();
        feeds[0].Key.Should().Be("acme/steps");
        feeds[0].Branch.Should().Be("develop");
        feeds[0].SubDir.Should().Be("step-templates", "SubDir defaults when omitted");
    }

    [Fact]
    public async Task Orphan_removal_is_scoped_to_the_feed_that_synced()
    {
        var (ownerA, ownerB) = (UniqueOwner(), UniqueOwner());
        var handler = new RoutingHandler();
        handler.SetTree(ownerA, ["step-templates/a-one.json"]);
        handler.SetTree(ownerB, ["step-templates/b-one.json"]);

        var svc = NewSvc(handler, TwoFeedConfig(ownerA, ownerB));

        (await svc.RefreshAsync()).Added.Should().Be(2);

        // Feed A empties upstream; feed B unchanged.
        handler.SetTree(ownerA, []);
        await svc.RefreshAsync();

        await using var db = postgres.CreateContext();
        (await db.StepTemplateCatalog.AsNoTracking()
                .CountAsync(e => e.FeedKey == $"{ownerA}/steps"))
            .Should().Be(0, "feed A's file was removed upstream");
        (await db.StepTemplateCatalog.AsNoTracking()
                .CountAsync(e => e.FeedKey == $"{ownerB}/steps"))
            .Should().Be(1, "feed B must be untouched by feed A's sync");
    }

    [Fact]
    public async Task A_failing_feed_neither_aborts_nor_orphan_deletes_the_healthy_one()
    {
        var (ownerA, ownerB) = (UniqueOwner(), UniqueOwner());
        var handler = new RoutingHandler();
        handler.SetTree(ownerA, ["step-templates/a-one.json"]);
        handler.SetTree(ownerB, ["step-templates/b-one.json"]);

        var svc = NewSvc(handler, TwoFeedConfig(ownerA, ownerB));
        await svc.RefreshAsync();

        // Feed A starts failing hard; feed B keeps working.
        handler.Fail(ownerA);
        var result = await svc.RefreshAsync();

        result.UpstreamCount.Should().Be(1, "only feed B contributed");

        await using var db = postgres.CreateContext();
        (await db.StepTemplateCatalog.AsNoTracking()
                .CountAsync(e => e.FeedKey == $"{ownerA}/steps"))
            .Should().Be(1, "a feed outage must never orphan-delete its cached rows");
    }

    [Fact]
    public async Task Refresh_throws_only_when_every_feed_fails()
    {
        var (ownerA, ownerB) = (UniqueOwner(), UniqueOwner());
        var handler = new RoutingHandler();
        handler.Fail(ownerA);
        handler.Fail(ownerB);

        var svc = NewSvc(handler, TwoFeedConfig(ownerA, ownerB));

        var act = () => svc.RefreshAsync();
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Every step-template feed failed*");
    }

    [Fact]
    public async Task Refresh_disabled_by_config_is_a_noop()
    {
        var handler = new RoutingHandler(); // would throw on any request
        var svc = NewSvc(handler, new Dictionary<string, string?>
        {
            ["StepTemplates:Catalog:Enabled"] = "false",
        });

        var result = await svc.RefreshAsync();
        result.UpstreamCount.Should().Be(0);
    }

    [Fact]
    public async Task ResolveFeeds_and_refresh_use_database_feeds_over_config()
    {
        var handler = new RoutingHandler();
        handler.SetTree("database-owner", []);
        var harness = NewSvcWithSettings(handler, new Dictionary<string, string?>
        {
            ["StepTemplates:Catalog:Feeds:0:Owner"] = "config-owner",
            ["StepTemplates:Catalog:Feeds:0:Repo"] = "config-repo",
            ["StepTemplates:Catalog:Enabled"] = "false",
        });
        await harness.Effective.SaveCatalogAsync(new CatalogSettingsUpdate
        {
            PackageCatalogEnabled = true,
            PackageCatalogOwner = "owner",
            PackageCatalogRepo = "repo",
            TemplateCatalogEnabled = true,
            TemplateCatalogFeeds =
            [
                new()
                {
                    Owner = "database-owner",
                    Repo = "database-repo",
                    Branch = "database-branch",
                    SubDir = "database-subdir",
                },
            ],
        });

        var feeds = await harness.Service.ResolveFeedsAsync();
        var result = await harness.Service.RefreshAsync();

        feeds.Should().ContainSingle();
        feeds[0].Should().Be(new StepTemplateCatalogService.Feed(
            "database-owner", "database-repo", "database-branch", "database-subdir"));
        result.UpstreamCount.Should().Be(0);
        handler.RequestedOwners.Should().Equal("database-owner");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string UniqueOwner() => "owner-" + Guid.NewGuid().ToString("N")[..8];

    private static Dictionary<string, string?> TwoFeedConfig(string ownerA, string ownerB) => new()
    {
        ["StepTemplates:Catalog:Feeds:0:Owner"] = ownerA,
        ["StepTemplates:Catalog:Feeds:0:Repo"]  = "steps",
        ["StepTemplates:Catalog:Feeds:1:Owner"] = ownerB,
        ["StepTemplates:Catalog:Feeds:1:Repo"]  = "steps",
    };

    private StepTemplateCatalogService NewSvc(
        HttpMessageHandler handler, Dictionary<string, string?>? feedsConfig)
        => NewSvcWithSettings(handler, feedsConfig).Service;

    private (StepTemplateCatalogService Service, EffectiveSettingsService Effective)
        NewSvcWithSettings(HttpMessageHandler handler, Dictionary<string, string?>? feedsConfig)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(feedsConfig ?? [])
            .Build();

        var settings = new SettingsService(postgres.ScopeFactory, TimeProvider.System);
        var effective = new EffectiveSettingsService(settings, config, TestCrypto.Service(MasterKey));
        var service = new StepTemplateCatalogService(
            postgres,
            new StubHttpClientFactory(new HttpClient(handler)),
            new StepTemplateService(postgres, new AllowAllPermissionEvaluator()),
            effective,
            Microsoft.Extensions.Options.Options.Create(new SsrfOptions()),
            NullLogger<StepTemplateCatalogService>.Instance,
            settings);
        return (service, effective);
    }

    private async Task DeleteCatalogSettingsAsync()
    {
        await using var db = postgres.CreateContext();
        await db.Set<Setting>().Where(s => s.Key == CatalogSettings.Key).ExecuteDeleteAsync();
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    /// <summary>
    /// Routes GitHub tree + raw requests per owner: trees list the configured
    /// paths, raw fetches return a minimal valid template JSON. Owners marked
    /// failed return 500 on their tree call.
    /// </summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string[]> _trees = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _failing = new(StringComparer.OrdinalIgnoreCase);

        public List<string> RequestedOwners { get; } = [];

        public void SetTree(string owner, string[] paths) => _trees[owner] = paths;
        public void Fail(string owner) => _failing.Add(owner);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;

            if (uri.Host == "api.github.com" && uri.AbsolutePath.Contains("/git/trees/"))
            {
                // /repos/{owner}/{repo}/git/trees/{branch}
                var owner = uri.AbsolutePath.Split('/')[2];
                RequestedOwners.Add(owner);
                if (_failing.Contains(owner))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                }

                var paths = _trees.GetValueOrDefault(owner, []);
                var blobs = string.Join(",", paths.Select(p =>
                    $$"""{ "type": "blob", "path": "{{p}}", "sha": "{{Sha(p)}}" }"""));
                return Json($$"""{ "tree": [{{blobs}}] }""");
            }

            if (uri.Host == "raw.githubusercontent.com")
            {
                // /{owner}/{repo}/{branch}/{path...} — unique id per file path.
                var id = Guid.NewGuid().ToString("N");
                var name = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
                return Json($$"""
                    { "Id": "{{id}}", "Name": "{{name}}",
                      "ActionType": "Octopus.Script", "Category": "script" }
                    """);
            }

            throw new NotSupportedException(
                $"Unexpected HTTP request in test: {request.Method} {uri}.");
        }

        private static string Sha(string path) =>
            // Not cryptographic — just a stable fake git blob SHA per path.
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(path)))[..40];

        private static Task<HttpResponseMessage> Json(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
