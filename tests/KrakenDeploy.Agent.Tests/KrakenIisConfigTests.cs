using FluentAssertions;
using KrakenDeploy.Agent.Deployment.Iis;
using KrakenDeploy.Contracts.Steps;

namespace KrakenDeploy.Agent.Tests;

public sealed class KrakenIisConfigTests
{
    // ── Required-key validation ─────────────────────────────────────────────────

    [Fact]
    public void Parse_throws_when_SiteName_missing()
    {
        var config = new Dictionary<string, string>
        {
            [KrakenIisConfigKeys.WebRoot] = @"C:\inetpub\app",
        };

        var act = () => KrakenIisConfig.Parse(config);
        act.Should().Throw<InvalidOperationException>().WithMessage("*SiteName*");
    }

    [Fact]
    public void Parse_throws_when_WebRoot_missing()
    {
        var config = new Dictionary<string, string>
        {
            [KrakenIisConfigKeys.SiteName] = "MySite",
        };

        var act = () => KrakenIisConfig.Parse(config);
        act.Should().Throw<InvalidOperationException>().WithMessage("*WebRoot*");
    }

    // ── Defaults ────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_applies_sensible_defaults_for_minimal_config()
    {
        var config = new Dictionary<string, string>
        {
            [KrakenIisConfigKeys.SiteName] = "MySite",
            [KrakenIisConfigKeys.WebRoot]  = @"C:\inetpub\mysite",
        };

        var cfg = KrakenIisConfig.Parse(config);

        cfg.SiteName.Should().Be("MySite");
        cfg.WebRoot.Should().Be(@"C:\inetpub\mysite");
        cfg.AppPath.Should().Be("/");

        // App pool defaults: name falls back to site name
        cfg.AppPool.Name.Should().Be("MySite");
        cfg.AppPool.RuntimeVersion.Should().Be("v4.0");
        cfg.AppPool.PipelineMode.Should().Be("Integrated");
        cfg.AppPool.Enable32Bit.Should().BeFalse();
        cfg.AppPool.LoadUserProfile.Should().BeFalse();
        cfg.AppPool.IdentityType.Should().Be("ApplicationPoolIdentity");
        cfg.AppPool.IdleTimeoutMinutes.Should().Be(20);

        // Recycle defaults
        cfg.Recycle.RegularIntervalMinutes.Should().Be(1740);
        cfg.Recycle.PrivateMemoryLimitKB.Should().BeNull();
        cfg.Recycle.SpecificTimes.Should().BeEmpty();
        cfg.Recycle.LogEventTime.Should().BeTrue();

        // Rapid-fail defaults
        cfg.RapidFail.Enabled.Should().BeTrue();
        cfg.RapidFail.MaxCrashesPerInterval.Should().Be(5);

        // Deploy defaults: AtomicSwap with retention
        cfg.Deploy.Mode.Should().Be("AtomicSwap");
        cfg.Deploy.IsAtomicSwap.Should().BeTrue();
        cfg.Deploy.KeepVersions.Should().Be(5);
        cfg.Deploy.DrainModeRecycle.Should().BeTrue();

        // Health check is opt-in (URL must be set)
        cfg.HealthCheck.Should().BeNull();

        cfg.Bindings.Should().BeEmpty();

        // Authentication defaults: anonymous on, others off (matches a fresh IIS site)
        cfg.Authentication.AnonymousEnabled.Should().BeTrue();
        cfg.Authentication.BasicEnabled.Should().BeFalse();
        cfg.Authentication.WindowsEnabled.Should().BeFalse();
    }

    [Fact]
    public void Parse_reads_authentication_toggles()
    {
        var config = new Dictionary<string, string>
        {
            [KrakenIisConfigKeys.SiteName]                          = "Web",
            [KrakenIisConfigKeys.WebRoot]                           = @"C:\inetpub\web",
            [KrakenIisConfigKeys.AuthenticationAnonymousEnabled]    = "false",
            [KrakenIisConfigKeys.AuthenticationBasicEnabled]        = "true",
            [KrakenIisConfigKeys.AuthenticationWindowsEnabled]      = "true",
        };

        var cfg = KrakenIisConfig.Parse(config);

        cfg.Authentication.AnonymousEnabled.Should().BeFalse();
        cfg.Authentication.BasicEnabled.Should().BeTrue();
        cfg.Authentication.WindowsEnabled.Should().BeTrue();
    }

    // ── Comprehensive parse ─────────────────────────────────────────────────────

    [Fact]
    public void Parse_reads_full_app_pool_and_recycle_config()
    {
        var config = new Dictionary<string, string>
        {
            [KrakenIisConfigKeys.SiteName] = "Web-Prod",
            [KrakenIisConfigKeys.WebRoot]  = @"D:\sites\webprod",

            [KrakenIisConfigKeys.AppPoolName]            = "WebProdPool",
            [KrakenIisConfigKeys.AppPoolRuntimeVersion]  = "",
            [KrakenIisConfigKeys.AppPoolPipelineMode]    = "Classic",
            [KrakenIisConfigKeys.AppPoolEnable32Bit]     = "true",
            [KrakenIisConfigKeys.AppPoolLoadUserProfile] = "true",
            [KrakenIisConfigKeys.AppPoolIdentityType]    = "SpecificUser",
            [KrakenIisConfigKeys.AppPoolUsername]        = @"DOMAIN\webuser",
            [KrakenIisConfigKeys.AppPoolPassword]        = "secret123",
            [KrakenIisConfigKeys.AppPoolIdleTimeoutMin]  = "0",
            [KrakenIisConfigKeys.AppPoolStartMode]       = "AlwaysRunning",

            [KrakenIisConfigKeys.RecycleRegularInterval]  = "60",
            [KrakenIisConfigKeys.RecyclePrivateMemoryKB]  = "1048576",
            [KrakenIisConfigKeys.RecycleRequestLimit]     = "100000",
            [KrakenIisConfigKeys.RecycleSpecificTimes]    = "02:00;14:00",
            [KrakenIisConfigKeys.RecycleLogEventOnDemand] = "false",
        };

        var cfg = KrakenIisConfig.Parse(config);

        cfg.AppPool.Name.Should().Be("WebProdPool");
        cfg.AppPool.RuntimeVersion.Should().Be("v4.0", "blank values fall back to default");
        cfg.AppPool.PipelineMode.Should().Be("Classic");
        cfg.AppPool.Enable32Bit.Should().BeTrue();
        cfg.AppPool.LoadUserProfile.Should().BeTrue();
        cfg.AppPool.IdentityType.Should().Be("SpecificUser");
        cfg.AppPool.Username.Should().Be(@"DOMAIN\webuser");
        cfg.AppPool.Password.Should().Be("secret123");
        cfg.AppPool.IdleTimeoutMinutes.Should().Be(0);
        cfg.AppPool.StartMode.Should().Be("AlwaysRunning");

        cfg.Recycle.RegularIntervalMinutes.Should().Be(60);
        cfg.Recycle.PrivateMemoryLimitKB.Should().Be(1048576);
        cfg.Recycle.RequestLimit.Should().Be(100000);
        cfg.Recycle.SpecificTimes.Should().HaveCount(2);
        cfg.Recycle.SpecificTimes[0].Should().Be(new TimeOnly(2, 0));
        cfg.Recycle.SpecificTimes[1].Should().Be(new TimeOnly(14, 0));
        cfg.Recycle.LogEventOnDemand.Should().BeFalse();
        cfg.Recycle.LogEventTime.Should().BeTrue("unspecified flags default to true");
    }

    [Fact]
    public void Parse_health_check_only_set_when_url_present()
    {
        var configWithUrl = new Dictionary<string, string>
        {
            [KrakenIisConfigKeys.SiteName] = "S",
            [KrakenIisConfigKeys.WebRoot]  = @"C:\s",
            [KrakenIisConfigKeys.HealthCheckUrl]               = "http://localhost/health",
            [KrakenIisConfigKeys.HealthCheckExpectedStatus]    = "204",
            [KrakenIisConfigKeys.HealthCheckRetryAttempts]     = "10",
            [KrakenIisConfigKeys.HealthCheckExpectedBodyContains] = "OK",
        };

        var cfg = KrakenIisConfig.Parse(configWithUrl);
        cfg.HealthCheck.Should().NotBeNull();
        cfg.HealthCheck!.Url.Should().Be("http://localhost/health");
        cfg.HealthCheck.ExpectedStatus.Should().Be(204);
        cfg.HealthCheck.RetryAttempts.Should().Be(10);
        cfg.HealthCheck.ExpectedBodyContains.Should().Be("OK");
    }
}

public sealed class KrakenIisBindingTests
{
    [Theory]
    [InlineData("http|*|80", "http", "*", 80, "")]
    [InlineData("http|10.0.0.5|8080|api.local", "http", "10.0.0.5", 8080, "api.local")]
    public void Parse_reads_basic_http_fields(
        string line, string proto, string ip, int port, string host)
    {
        var b = KrakenIisBinding.Parse(line);

        b.Protocol.Should().Be(proto);
        b.IpAddress.Should().Be(ip);
        b.Port.Should().Be(port);
        b.Hostname.Should().Be(host);
        b.IsHttps.Should().BeFalse();
        b.BindingInformation.Should().Be($"{ip}:{port}:{host}");
    }

    [Fact]
    public void Parse_reads_full_https_binding_with_cert_and_sni()
    {
        var b = KrakenIisBinding.Parse(
            "https|*|443|app.example.com|ABCDEF1234567890|My|true|1");

        b.Protocol.Should().Be("https");
        b.IsHttps.Should().BeTrue();
        b.Port.Should().Be(443);
        b.Hostname.Should().Be("app.example.com");
        b.CertThumbprint.Should().Be("ABCDEF1234567890");
        b.CertStore.Should().Be("My");
        b.SniRequired.Should().BeTrue();
        b.SslFlags.Should().Be(1);
    }

    [Fact]
    public void Parse_throws_on_invalid_port()
    {
        var act = () => KrakenIisBinding.Parse("http|*|notaport");
        act.Should().Throw<FormatException>().WithMessage("*port*");
    }

    [Fact]
    public void ParseAll_reads_multiple_lines_skipping_blanks_and_comments()
    {
        var raw = """
            # Production bindings
            http|*|80

            https|*|443|app.example.com|AAA|My|true|1
            """;

        var bindings = KrakenIisBinding.ParseAll(raw);
        bindings.Should().HaveCount(2);
        bindings[0].Protocol.Should().Be("http");
        bindings[1].Protocol.Should().Be("https");
        bindings[1].Hostname.Should().Be("app.example.com");
    }

    [Fact]
    public void ParseAll_returns_empty_for_null_or_blank()
    {
        KrakenIisBinding.ParseAll(null).Should().BeEmpty();
        KrakenIisBinding.ParseAll("").Should().BeEmpty();
        KrakenIisBinding.ParseAll("   ").Should().BeEmpty();
    }
}

public sealed class KrakenIisStepHandlerTests
{
    [Theory]
    [InlineData("Kraken.IIS")]
    [InlineData("kraken.iis")]
    [InlineData("Octopus.IIS")]
    [InlineData("octopus.iis")]
    public void CanHandle_accepts_kraken_and_octopus_iis_case_insensitive(string stepType)
    {
        var handler = new KrakenIisStepHandler(null!);
        handler.CanHandle(stepType).Should().BeTrue();
    }

    [Fact]
    public void CanHandle_rejects_unrelated_step_types()
    {
        var handler = new KrakenIisStepHandler(null!);
        handler.CanHandle("Kraken.Script").Should().BeFalse();
        handler.CanHandle("Octopus.Script").Should().BeFalse();
        handler.CanHandle("Octopus.WindowsService").Should().BeFalse();
    }

    [Fact]
    public void RequiresPackage_is_true()
    {
        var handler = new KrakenIisStepHandler(null!);
        handler.RequiresPackage.Should().BeTrue();
    }
}
