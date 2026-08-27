using System.Security.Cryptography;
using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Platform;
using KrakenDeploy.Server.Core.Domain.Settings;
using KrakenDeploy.Server.Data.Net;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Data.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KrakenDeploy.Server.Data.Tests;

[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class EffectiveSettingsServiceTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly string Base64Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateContext();
        await db.Set<Setting>().ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Engine_resolves_default_then_file_then_database()
    {
        var defaults = NewService([]);
        var defaultValue = await defaults.GetEngineAsync();
        defaultValue.MaxConcurrentTasks.Should().Be(new EffectiveSetting<int>(20, SettingValueSource.Default));

        var file = NewService(new Dictionary<string, string?>
        {
            ["Engine:MaxConcurrentTasks"] = "31",
        });
        var fileValue = await file.GetEngineAsync();
        fileValue.MaxConcurrentTasks.Should().Be(new EffectiveSetting<int>(31, SettingValueSource.ConfigurationFile));

        await file.SaveEngineAsync(new EngineSettings { MaxConcurrentTasks = 42 });
        var databaseValue = await file.GetEngineAsync();
        databaseValue.MaxConcurrentTasks.Should().Be(new EffectiveSetting<int>(42, SettingValueSource.Database));
    }

    [Fact]
    public async Task Ssrf_each_configuration_property_pins_independently_over_database()
    {
        var databaseService = NewService([]);
        await databaseService.SaveSsrfAsync(new SsrfSettings
        {
            Webhook = new SsrfPolicySettings
            {
                AllowLoopback = true,
                AllowPrivate = true,
                AllowedHosts = ["hooks.internal"],
            },
        });

        var loopbackPin = await NewService(new Dictionary<string, string?>
        {
            ["Ssrf:Webhook:AllowLoopback"] = "false",
        }).GetSsrfAsync();
        loopbackPin.Webhook.AllowLoopback.Should().Be(
            new EffectiveSetting<bool>(false, SettingValueSource.ConfigurationFile));
        loopbackPin.Webhook.AllowPrivate.Should().Be(
            new EffectiveSetting<bool>(true, SettingValueSource.Database));
        loopbackPin.Webhook.AllowedHosts.Should().BeEquivalentTo(
            new EffectiveSetting<string[]>(["hooks.internal"], SettingValueSource.Database));

        var privatePin = await NewService(new Dictionary<string, string?>
        {
            ["Ssrf:Webhook:AllowPrivate"] = "false",
        }).GetSsrfAsync();
        privatePin.Webhook.AllowPrivate.Should().Be(
            new EffectiveSetting<bool>(false, SettingValueSource.ConfigurationFile));
        privatePin.Webhook.AllowLoopback.Should().Be(
            new EffectiveSetting<bool>(true, SettingValueSource.Database));
        privatePin.Webhook.AllowedHosts.Should().BeEquivalentTo(
            new EffectiveSetting<string[]>(["hooks.internal"], SettingValueSource.Database));

        var hostsPin = await NewService(new Dictionary<string, string?>
        {
            ["Ssrf:Webhook:AllowedHosts:0"] = "pinned.internal",
        }).GetSsrfAsync();
        hostsPin.Webhook.AllowedHosts.Source.Should().Be(SettingValueSource.ConfigurationFile);
        hostsPin.Webhook.AllowedHosts.Value.Should().Equal("pinned.internal");
        hostsPin.Webhook.AllowLoopback.Should().Be(
            new EffectiveSetting<bool>(true, SettingValueSource.Database));
        hostsPin.Webhook.AllowPrivate.Should().Be(
            new EffectiveSetting<bool>(true, SettingValueSource.Database));
    }

    [Fact]
    public async Task Ssrf_startup_snapshot_converts_every_effective_policy()
    {
        var service = NewService(new Dictionary<string, string?>
        {
            ["Ssrf:Webhook:AllowPrivate"] = "true",
            ["Ssrf:StepCatalog:AllowedHosts:0"] = "catalog.internal",
            ["Ssrf:Oidc:AllowLoopback"] = "true",
            ["Ssrf:Ai:AllowLoopback"] = "false",
            ["Ssrf:Ai:AllowedHosts:0"] = "ai.internal",
        });

        var snapshot = await service.GetSsrfOptionsSnapshotAsync();

        snapshot.Value.Webhook.AllowPrivate.Should().BeTrue();
        snapshot.Value.StepCatalog.AllowedHosts.Should().Equal("catalog.internal");
        snapshot.Value.Oidc.AllowLoopback.Should().BeTrue();
        snapshot.Value.Ai.AllowLoopback.Should().BeFalse();
        snapshot.Value.Ai.AllowedHosts.Should().Equal("ai.internal");
    }

    [Fact]
    public void Ssrf_policy_settings_conversion_preserves_values_and_copies_hosts()
    {
        var settings = new SsrfPolicySettings
        {
            AllowLoopback = true,
            AllowPrivate = true,
            AllowedHosts = ["service.internal", "10.20.0.0/16"],
        };

        var policy = settings.ToSsrfPolicy();

        policy.AllowLoopback.Should().BeTrue();
        policy.AllowPrivate.Should().BeTrue();
        policy.AllowedHosts.Should().Equal(settings.AllowedHosts);
        policy.AllowedHosts.Should().NotBeSameAs(settings.AllowedHosts);
    }

    [Fact]
    public void Validation_rejects_unsafe_engine_operational_and_ssrf_values()
    {
        var engine = new EngineSettings { AgentDisconnectWaveGrace = TimeSpan.FromSeconds(30) };
        var operational = new OperationalSettings { ServerBaseUrl = "ftp://example.com" };
        var ssrf = new SsrfPolicySettings { AllowedHosts = ["169.254.169.254"] };

        FluentActions.Invoking(() => EffectiveSettingsService.ValidateEngine(engine))
            .Should().Throw<ArgumentException>().WithMessage("*greater than 30 seconds*");
        FluentActions.Invoking(() => EffectiveSettingsService.ValidateOperational(operational))
            .Should().Throw<ArgumentException>().WithMessage("*absolute http(s) URL*");
        FluentActions.Invoking(() => EffectiveSettingsService.ValidateOperational(
                new OperationalSettings { AgentTokenLifetimeDays = 3651 }))
            .Should().Throw<ArgumentException>().WithMessage("*between 1 and 3650*");
        FluentActions.Invoking(() => EffectiveSettingsService.ValidateSsrfPolicy("Webhook", ssrf))
            .Should().Throw<ArgumentException>().WithMessage("*hard-blocked*");
    }

    [Fact]
    public async Task Catalog_encrypts_token_and_blank_input_preserves_it_without_exposing_ciphertext()
    {
        var service = NewService([]);
        var update = SampleCatalogUpdate();
        update.GitHubToken = "github-secret";
        await service.SaveCatalogAsync(update);

        var first = await service.GetCatalogAsync();
        first.HasGitHubToken.Value.Should().BeTrue();
        first.GetType().GetProperties().Should().NotContain(p => p.Name.EndsWith("Encrypted", StringComparison.Ordinal));
        (await service.GetGitHubTokenAsync()).Should().Be("github-secret");

        update.PackageCatalogRepo = "changed-repo";
        update.GitHubToken = "";
        await service.SaveCatalogAsync(update);

        (await service.GetGitHubTokenAsync()).Should().Be("github-secret");
        await using var db = postgres.CreateContext();
        var stored = await SettingsService.ReadOrDefaultAsync<CatalogSettings>(db);
        stored.GitHubTokenEncrypted.Should().NotBeNullOrEmpty().And.NotBe("github-secret");
        SettingsDocumentCatalog.Find(CatalogSettings.Key)!.EncryptedMembers
            .Should().ContainSingle(p => p.Name == nameof(CatalogSettings.GitHubTokenEncrypted));
    }

    [Fact]
    public async Task Catalog_blank_token_preserves_configuration_fallback_and_clear_suppresses_it()
    {
        var service = NewService(new Dictionary<string, string?>
        {
            ["GitHub:Token"] = "configuration-token",
        });
        var update = SampleCatalogUpdate();

        await service.SaveCatalogAsync(update);

        (await service.GetGitHubTokenAsync()).Should().Be("configuration-token");
        (await service.GetCatalogAsync()).HasGitHubToken.Should().Be(
            new EffectiveSetting<bool>(true, SettingValueSource.ConfigurationFile));

        update.ClearGitHubToken = true;
        await service.SaveCatalogAsync(update);

        (await service.GetGitHubTokenAsync()).Should().BeNull();
        (await service.GetCatalogAsync()).HasGitHubToken.Should().Be(
            new EffectiveSetting<bool>(false, SettingValueSource.Database));
    }

    [Fact]
    public async Task Ssrf_save_does_not_replace_database_value_hidden_by_file_pin()
    {
        var unpinned = NewService([]);
        await unpinned.SaveSsrfAsync(new SsrfSettings
        {
            Webhook = new SsrfPolicySettings { AllowLoopback = true },
        });
        var pinned = NewService(new Dictionary<string, string?>
        {
            ["Ssrf:Webhook:AllowLoopback"] = "false",
        });

        await pinned.SaveSsrfAsync(new SsrfSettings
        {
            Webhook = new SsrfPolicySettings
            {
                AllowLoopback = false,
                AllowPrivate = true,
            },
        });

        await using var db = postgres.CreateContext();
        var stored = await SettingsService.ReadOrDefaultAsync<SsrfSettings>(db);
        stored.Webhook.AllowLoopback.Should().BeTrue("the file pin only masks the DB value");
        stored.Webhook.AllowPrivate.Should().BeTrue("unpinned fields are saved normally");
    }

    [Fact]
    public async Task GitHub_client_authentication_reads_the_current_decrypted_token()
    {
        var service = NewService([]);
        var update = SampleCatalogUpdate();
        update.GitHubToken = "first-token";
        await service.SaveCatalogAsync(update);

        var recorder = new AuthorizationRecordingHandler();
        using var client = new HttpClient(recorder);

        await GitHubHttpClientAuthentication.ApplyAsync(
            client, service, new Uri("https://api.github.com/first"), default);
        await client.GetAsync("https://api.github.com/first");
        update.GitHubToken = "second-token";
        await service.SaveCatalogAsync(update);
        await GitHubHttpClientAuthentication.ApplyAsync(
            client, service, new Uri("https://api.github.com/second"), default);
        await client.GetAsync("https://api.github.com/second");
        await GitHubHttpClientAuthentication.ApplyAsync(
            client, service, new Uri("https://downloads.example.test/package"), default);
        await client.GetAsync("https://downloads.example.test/package");

        recorder.BearerTokens.Should().Equal("first-token", "second-token", null);
    }

    private EffectiveSettingsService NewService(
        Dictionary<string, string?> values,
        DeploymentTopology topology = DeploymentTopology.OnPrem)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new EffectiveSettingsService(
            new SettingsService(postgres.ScopeFactory, TimeProvider.System),
            config,
            TestCrypto.Service(Base64Key),
            new DeploymentOptions { Topology = topology });
    }

    private static CatalogSettingsUpdate SampleCatalogUpdate() => new()
    {
        PackageCatalogEnabled = true,
        PackageCatalogOwner = "DomagojJugovich",
        PackageCatalogRepo = "kraken-steps",
        TemplateCatalogEnabled = true,
        TemplateCatalogFeeds =
        [
            new CatalogFeedSettings
            {
                Owner = "OctopusDeploy",
                Repo = "Library",
                Branch = "master",
                SubDir = "step-templates",
            },
        ],
    };

    // ── BG1/T2: the host-settings tenancy gate keys on the TOPOLOGY ──────────
    //
    // F3 made host-wide Engine/operational/SSRF settings configuration-only under
    // multi-account ("one tenant cannot change process-wide policy"). BG1 re-keyed
    // that check from the removed MultiAccount:Enabled config value to
    // DeploymentOptions.Topology, so these pin the mapping — a silent regression
    // here would either let a tenant rewrite process-wide policy (Saas) or lock an
    // on-prem operator out of their own settings GUI.

    [Theory]
    [InlineData(DeploymentTopology.OnPrem)]
    [InlineData(DeploymentTopology.OnPremBlueGreen)]
    public async Task Host_settings_are_editable_under_the_single_tenant_topologies(
        DeploymentTopology topology)
    {
        var service = NewService([], topology);

        // Saves go through (no InvalidOperationException from the tenancy gate).
        await service.SaveEngineAsync(new EngineSettings());
        await service.SaveOperationalAsync(new OperationalSettings());
        await service.SaveSsrfAsync(new SsrfSettings());
    }

    [Fact]
    public async Task Host_settings_are_configuration_only_under_Saas()
    {
        var service = NewService([], DeploymentTopology.Saas);

        var engine = () => service.SaveEngineAsync(new EngineSettings());
        await engine.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*configuration-only*",
                "under Saas one tenant must not change process-wide policy");

        var operational = () => service.SaveOperationalAsync(new OperationalSettings());
        await operational.Should().ThrowAsync<InvalidOperationException>();

        var ssrf = () => service.SaveSsrfAsync(new SsrfSettings());
        await ssrf.Should().ThrowAsync<InvalidOperationException>();
    }

    private sealed class AuthorizationRecordingHandler : HttpMessageHandler
    {
        public List<string?> BearerTokens { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            BearerTokens.Add(request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
