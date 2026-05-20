using FluentAssertions;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Steps.KrakenIis;
using Octostache;

namespace KrakenDeploy.Steps.KrakenIis.Tests;

/// <summary>
/// Unit tests for <see cref="OctopusIisConfig.MapToKrakenIisConfig"/> — the
/// translator that converts an <c>Octopus.IIS</c> property bag into the existing
/// <see cref="KrakenIisConfig"/> shape so <c>IisScriptGenerator</c> stays the
/// single code path for both shapes.
/// </summary>
public sealed class OctopusIisConfigTests
{
    // ── Shape detection ────────────────────────────────────────────────────

    [Fact]
    public void IsOctopusShape_returns_true_when_WebSiteName_is_present()
    {
        var config = new Dictionary<string, string>
        {
            [OctopusIisConfigKeys.WebSiteName] = "MySite",
        };
        OctopusIisConfig.IsOctopusShape(config).Should().BeTrue();
    }

    [Fact]
    public void IsOctopusShape_returns_true_for_webApplication_and_virtualDirectory_shape_keys()
    {
        OctopusIisConfig.IsOctopusShape(new Dictionary<string, string>
        {
            [OctopusIisConfigKeys.WebApplicationCreateOrUpdate] = "True",
        }).Should().BeTrue();

        OctopusIisConfig.IsOctopusShape(new Dictionary<string, string>
        {
            [OctopusIisConfigKeys.VirtualDirectoryCreateOrUpdate] = "True",
        }).Should().BeTrue();
    }

    [Fact]
    public void IsOctopusShape_returns_false_for_pure_kraken_shape()
    {
        var config = new Dictionary<string, string>
        {
            [KrakenIisConfigKeys.SiteName] = "MySite",
            [KrakenIisConfigKeys.WebRoot]  = @"C:\inetpub\wwwroot\MySite",
        };
        OctopusIisConfig.IsOctopusShape(config).Should().BeFalse();
    }

    // ── webSite happy path ────────────────────────────────────────────────

    [Fact]
    public void MapToKrakenIisConfig_translates_a_minimal_webSite_step()
    {
        var config = new Dictionary<string, string>
        {
            [OctopusIisConfigKeys.DeploymentType] = "webSite",
            [OctopusIisConfigKeys.WebSiteName]    = "WebArgosy_Prod",
            [OctopusIisConfigKeys.ApplicationPoolName] = "WebArgosy_Prod_Pool",
            [OctopusIisConfigKeys.ApplicationPoolFrameworkVersion] = "v4.0",
            [OctopusIisConfigKeys.PackageCustomInstallationDirectory] = @"C:\Apps\WebArgosy_Prod",
        };

        var result = OctopusIisConfig.MapToKrakenIisConfig(config,
            octostache: PassThrough, fallbackWebRoot: @"C:\fallback");

        result.WebSite!.SiteName.Should().Be("WebArgosy_Prod");
        result.WebSite!.WebRoot.Should().Be(@"C:\Apps\WebArgosy_Prod");
        result.WebSite!.AppPool.Name.Should().Be("WebArgosy_Prod_Pool");
        result.WebSite!.AppPool.RuntimeVersion.Should().Be("v4.0");
        // Octopus.IIS shape always lands as InPlace deploy — atomic-swap is a Kraken extra.
        result.WebSite!.Deploy.IsAtomicSwap.Should().BeFalse();
    }

    [Fact]
    public void MapToKrakenIisConfig_uses_fallback_webRoot_when_CustomInstallationDirectory_absent()
    {
        var config = new Dictionary<string, string>
        {
            [OctopusIisConfigKeys.WebSiteName] = "Site",
        };

        var result = OctopusIisConfig.MapToKrakenIisConfig(config,
            octostache: PassThrough, fallbackWebRoot: @"C:\extract\xyz");

        result.WebSite!.WebRoot.Should().Be(@"C:\extract\xyz");
    }

    [Fact]
    public void MapToKrakenIisConfig_octostache_evaluates_site_name_and_install_dir()
    {
        var config = new Dictionary<string, string>
        {
            [OctopusIisConfigKeys.WebSiteName] = "Site_#{Octopus.Environment.Name}",
            [OctopusIisConfigKeys.PackageCustomInstallationDirectory] = @"C:\Apps\Site_#{Octopus.Environment.Name}",
        };

        var vars = new VariableDictionary { ["Octopus.Environment.Name"] = "Production" };
        var result = OctopusIisConfig.MapToKrakenIisConfig(config,
            octostache: vars.Evaluate, fallbackWebRoot: @"C:\fallback");

        result.WebSite!.SiteName.Should().Be("Site_Production");
        result.WebSite!.WebRoot.Should().Be(@"C:\Apps\Site_Production");
    }

    [Fact]
    public void MapToKrakenIisConfig_maps_SpecificUser_appPool_identity_and_credentials()
    {
        var config = new Dictionary<string, string>
        {
            [OctopusIisConfigKeys.WebSiteName] = "Site",
            [OctopusIisConfigKeys.ApplicationPoolIdentityType] = "SpecificUser",
            [OctopusIisConfigKeys.ApplicationPoolUsername]     = "DOMAIN\\Svc",
            [OctopusIisConfigKeys.ApplicationPoolPassword]     = "Sup3rs3cret",
        };

        var result = OctopusIisConfig.MapToKrakenIisConfig(config,
            octostache: PassThrough, fallbackWebRoot: @"C:\fallback");

        result.WebSite!.AppPool.IdentityType.Should().Be("SpecificUser");
        result.WebSite!.AppPool.Username.Should().Be("DOMAIN\\Svc");
        result.WebSite!.AppPool.Password.Should().Be("Sup3rs3cret");
    }

    [Fact]
    public void MapToKrakenIisConfig_warns_and_drops_password_when_value_is_sensitive_envelope()
    {
        // Octopus emits sensitive properties as {"HasValue":...,"NewValue":...,"Hint":...}.
        // The B-2 importer preserves the envelope as JSON text; the mapper must detect it.
        var config = new Dictionary<string, string>
        {
            [OctopusIisConfigKeys.WebSiteName] = "Site",
            [OctopusIisConfigKeys.ApplicationPoolIdentityType] = "SpecificUser",
            [OctopusIisConfigKeys.ApplicationPoolUsername]     = "DOMAIN\\Svc",
            [OctopusIisConfigKeys.ApplicationPoolPassword]     =
                "{\"HasValue\":true,\"NewValue\":null,\"Hint\":null}",
        };

        var result = OctopusIisConfig.MapToKrakenIisConfig(config,
            octostache: PassThrough, fallbackWebRoot: @"C:\fallback");

        result.WebSite!.AppPool.Password.Should().BeNullOrEmpty(
            "the sensitive-value envelope is metadata, not a real password");
        result.Warnings.Should().Contain(w => w.Contains("sensitive-value envelope"));
    }

    [Fact]
    public void MapToKrakenIisConfig_propagates_auth_toggles_into_KrakenIisAuthentication()
    {
        // Octopus exports the three toggles as "True"/"False" strings. The mapper
        // forwards them so Kraken.IIS authentic Set-WebConfigurationProperty
        // emit covers all three modules.
        var config = new Dictionary<string, string>
        {
            [OctopusIisConfigKeys.WebSiteName]            = "Site",
            [OctopusIisConfigKeys.EnableAnonymousAuth]    = "False",
            [OctopusIisConfigKeys.EnableBasicAuth]        = "True",
            [OctopusIisConfigKeys.EnableWindowsAuth]      = "True",
        };

        var result = OctopusIisConfig.MapToKrakenIisConfig(config,
            octostache: PassThrough, fallbackWebRoot: @"C:\fallback");

        result.WebSite!.Authentication.AnonymousEnabled.Should().BeFalse();
        result.WebSite!.Authentication.BasicEnabled.Should().BeTrue();
        result.WebSite!.Authentication.WindowsEnabled.Should().BeTrue();
    }

    [Fact]
    public void MapToKrakenIisConfig_auth_toggles_absent_means_kraken_defaults()
    {
        var config = new Dictionary<string, string>
        {
            [OctopusIisConfigKeys.WebSiteName] = "Site",
            // No auth keys set.
        };

        var result = OctopusIisConfig.MapToKrakenIisConfig(config,
            octostache: PassThrough, fallbackWebRoot: @"C:\fallback");

        result.WebSite!.Authentication.AnonymousEnabled.Should().BeTrue("Kraken default");
        result.WebSite!.Authentication.BasicEnabled.Should().BeFalse("Kraken default");
        result.WebSite!.Authentication.WindowsEnabled.Should().BeFalse("Kraken default");
    }

    // ── Bindings ──────────────────────────────────────────────────────────

    [Fact]
    public void MapToKrakenIisConfig_translates_a_single_http_binding()
    {
        var bindings = """
            [{"protocol":"http","ipAddress":"*","port":"80","host":"app.example.com",
              "thumbprint":null,"certificateVariable":null,"requireSni":false,"enabled":true}]
            """;
        var config = new Dictionary<string, string>
        {
            [OctopusIisConfigKeys.WebSiteName] = "Site",
            [OctopusIisConfigKeys.Bindings]    = bindings,
        };

        var result = OctopusIisConfig.MapToKrakenIisConfig(config,
            octostache: PassThrough, fallbackWebRoot: @"C:\fallback");

        result.WebSite!.Bindings.Should().HaveCount(1);
        var b = result.WebSite!.Bindings[0];
        b.Protocol.Should().Be("http");
        b.Port.Should().Be(80);
        b.Hostname.Should().Be("app.example.com");
    }

    [Fact]
    public void MapToKrakenIisConfig_skips_disabled_bindings()
    {
        var bindings = """
            [
              {"protocol":"http","ipAddress":"*","port":"80","host":"keep","thumbprint":null,"requireSni":false,"enabled":true},
              {"protocol":"http","ipAddress":"*","port":"80","host":"drop","thumbprint":null,"requireSni":false,"enabled":false}
            ]
            """;
        var config = new Dictionary<string, string>
        {
            [OctopusIisConfigKeys.WebSiteName] = "Site",
            [OctopusIisConfigKeys.Bindings]    = bindings,
        };

        var result = OctopusIisConfig.MapToKrakenIisConfig(config,
            octostache: PassThrough, fallbackWebRoot: @"C:\fallback");

        result.WebSite!.Bindings.Should().HaveCount(1);
        result.WebSite!.Bindings[0].Hostname.Should().Be("keep");
    }

    [Fact]
    public void MapToKrakenIisConfig_octostache_evaluates_enabled_flag_before_drop_check()
    {
        // Real WebArgosy bindings use Octostache-conditional enabled flags
        // like `#{if SSLEnabled == "true"}True#{else}False#{/if}`.
        var bindings = """
            [
              {"protocol":"http","ipAddress":"*","port":"80","host":"keep","thumbprint":null,"requireSni":false,"enabled":"#{if SSLEnabled == \"true\"}True#{else}False#{/if}"}
            ]
            """;
        var config = new Dictionary<string, string>
        {
            [OctopusIisConfigKeys.WebSiteName] = "Site",
            [OctopusIisConfigKeys.Bindings]    = bindings,
        };

        var disabledVars = new VariableDictionary { ["SSLEnabled"] = "false" };
        var disabled = OctopusIisConfig.MapToKrakenIisConfig(config,
            octostache: disabledVars.Evaluate, fallbackWebRoot: @"C:\fallback");
        disabled.WebSite!.Bindings.Should().BeEmpty(
            "the Octostache conditional resolves to False, so the binding is disabled");

        var enabledVars = new VariableDictionary { ["SSLEnabled"] = "true" };
        var enabled = OctopusIisConfig.MapToKrakenIisConfig(config,
            octostache: enabledVars.Evaluate, fallbackWebRoot: @"C:\fallback");
        enabled.WebSite!.Bindings.Should().HaveCount(1);
    }

    [Fact]
    public void MapToKrakenIisConfig_translates_https_binding_with_thumbprint_and_sni()
    {
        var bindings = """
            [{"protocol":"https","ipAddress":"*","port":"443","host":"app.example.com",
              "thumbprint":"ABCDEF1234","certificateVariable":null,"requireSni":true,"enabled":true}]
            """;
        var config = new Dictionary<string, string>
        {
            [OctopusIisConfigKeys.WebSiteName] = "Site",
            [OctopusIisConfigKeys.Bindings]    = bindings,
        };

        var result = OctopusIisConfig.MapToKrakenIisConfig(config,
            octostache: PassThrough, fallbackWebRoot: @"C:\fallback");

        result.WebSite!.Bindings.Should().HaveCount(1);
        var b = result.WebSite!.Bindings[0];
        b.Protocol.Should().Be("https");
        b.Port.Should().Be(443);
        b.CertThumbprint.Should().Be("ABCDEF1234");
        b.CertStore.Should().Be("My");
        b.SniRequired.Should().BeTrue();
    }

    // ── Unsupported deployment types ──────────────────────────────────────

    [Fact]
    public void MapToKrakenIisConfig_maps_webApplication_into_KrakenIisWebApplicationConfig()
    {
        // Mirrors the real WebArgosy "ARR SITE Virtual Folder APP" step.
        var config = new Dictionary<string, string>
        {
            [OctopusIisConfigKeys.DeploymentType]                                = "webApplication",
            [OctopusIisConfigKeys.WebApplicationCreateOrUpdate]                  = "True",
            [OctopusIisConfigKeys.WebApplicationWebSiteName]                     = "WebArgosyOD_#{Octopus.Environment.Name}_ARR",
            [OctopusIisConfigKeys.WebApplicationVirtualPath]                     = "#{ArrVirtualPath}",
            [OctopusIisConfigKeys.WebApplicationApplicationPoolName]             = "WebArgosyOD#{Octopus.Environment.Name}_ARR",
            [OctopusIisConfigKeys.WebApplicationApplicationPoolFrameworkVersion] = "v4.0",
            [OctopusIisConfigKeys.WebApplicationApplicationPoolIdentityType]     = "ApplicationPoolIdentity",
            [OctopusIisConfigKeys.PackageCustomInstallationDirectory] =
                @"C:\Apps\WebArgosy_#{Octopus.Environment.Name}_ARR\#{ArrVirtualPath}",
        };

        var vars = new VariableDictionary
        {
            ["Octopus.Environment.Name"] = "Production",
            ["ArrVirtualPath"] = "/ArrWeb",
        };

        var result = OctopusIisConfig.MapToKrakenIisConfig(
            config, vars.Evaluate, fallbackWebRoot: @"C:\fallback");

        result.WebApplication.Should().NotBeNull();
        result.WebSite.Should().BeNull();
        result.VirtualDirectory.Should().BeNull();

        var w = result.WebApplication!;
        w.ParentSiteName.Should().Be("WebArgosyOD_Production_ARR");
        w.VirtualPath.Should().Be("/ArrWeb");
        w.PhysicalPath.Should().Be(@"C:\Apps\WebArgosy_Production_ARR\/ArrWeb");
        // Real Octopus template has no underscore between "OD" and the
        // Environment.Name placeholder, so the substituted value is run-on.
        w.AppPool.Name.Should().Be("WebArgosyODProduction_ARR");
        w.AppPool.RuntimeVersion.Should().Be("v4.0");
        w.AppPool.IdentityType.Should().Be("ApplicationPoolIdentity");
    }

    [Fact]
    public void MapToKrakenIisConfig_webApplication_missing_parent_site_throws()
    {
        var config = new Dictionary<string, string>
        {
            [OctopusIisConfigKeys.DeploymentType] = "webApplication",
            [OctopusIisConfigKeys.WebApplicationVirtualPath] = "/sub",
            // no WebSiteName
        };

        var act = () => OctopusIisConfig.MapToKrakenIisConfig(config,
            PassThrough, fallbackWebRoot: @"C:\fallback");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*WebApplication.WebSiteName*");
    }

    [Fact]
    public void MapToKrakenIisConfig_webApplication_missing_virtual_path_throws()
    {
        var config = new Dictionary<string, string>
        {
            [OctopusIisConfigKeys.DeploymentType] = "webApplication",
            [OctopusIisConfigKeys.WebApplicationWebSiteName] = "ParentSite",
            // no VirtualPath
        };

        var act = () => OctopusIisConfig.MapToKrakenIisConfig(config,
            PassThrough, fallbackWebRoot: @"C:\fallback");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*WebApplication.VirtualPath*");
    }

    [Fact]
    public void MapToKrakenIisConfig_webApplication_sensitive_password_envelope_warns_and_drops()
    {
        var config = new Dictionary<string, string>
        {
            [OctopusIisConfigKeys.DeploymentType]                                = "webApplication",
            [OctopusIisConfigKeys.WebApplicationWebSiteName]                     = "Parent",
            [OctopusIisConfigKeys.WebApplicationVirtualPath]                     = "/sub",
            [OctopusIisConfigKeys.WebApplicationApplicationPoolIdentityType]     = "SpecificUser",
            [OctopusIisConfigKeys.WebApplicationApplicationPoolUsername]         = "DOMAIN\\Svc",
            [OctopusIisConfigKeys.WebApplicationApplicationPoolPassword]         =
                "{\"HasValue\":true,\"NewValue\":null,\"Hint\":null}",
        };

        var result = OctopusIisConfig.MapToKrakenIisConfig(
            config, PassThrough, fallbackWebRoot: @"C:\fallback");

        result.WebApplication!.AppPool.Password.Should().BeNull();
        result.Warnings.Should().Contain(w =>
            w.Contains("WebApplication.ApplicationPoolPassword") &&
            w.Contains("sensitive-value envelope"));
    }

    [Fact]
    public void MapToKrakenIisConfig_maps_virtualDirectory_into_KrakenIisVirtualDirectoryConfig()
    {
        var config = new Dictionary<string, string>
        {
            [OctopusIisConfigKeys.DeploymentType]                = "virtualDirectory",
            [OctopusIisConfigKeys.VirtualDirectoryCreateOrUpdate] = "True",
            [OctopusIisConfigKeys.VirtualDirectoryWebSiteName]    = "WebArgosyOD_Production_ARR",
            [OctopusIisConfigKeys.VirtualDirectoryVirtualPath]    = "/static-content",
            [OctopusIisConfigKeys.PackageCustomInstallationDirectory] = @"C:\static-content",
        };

        var result = OctopusIisConfig.MapToKrakenIisConfig(
            config, PassThrough, fallbackWebRoot: @"C:\fallback");

        result.VirtualDirectory.Should().NotBeNull();
        result.WebSite.Should().BeNull();
        result.WebApplication.Should().BeNull();

        var v = result.VirtualDirectory!;
        v.ParentSiteName.Should().Be("WebArgosyOD_Production_ARR");
        v.VirtualPath.Should().Be("/static-content");
        v.PhysicalPath.Should().Be(@"C:\static-content");
    }

    [Fact]
    public void MapToKrakenIisConfig_virtualDirectory_missing_parent_site_throws()
    {
        var config = new Dictionary<string, string>
        {
            [OctopusIisConfigKeys.DeploymentType] = "virtualDirectory",
            [OctopusIisConfigKeys.VirtualDirectoryVirtualPath] = "/sub",
            // no WebSiteName
        };

        var act = () => OctopusIisConfig.MapToKrakenIisConfig(config,
            PassThrough, fallbackWebRoot: @"C:\fallback");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*VirtualDirectory.WebSiteName*");
    }

    [Fact]
    public void MapToKrakenIisConfig_virtualDirectory_missing_virtual_path_throws()
    {
        var config = new Dictionary<string, string>
        {
            [OctopusIisConfigKeys.DeploymentType] = "virtualDirectory",
            [OctopusIisConfigKeys.VirtualDirectoryWebSiteName] = "Parent",
            // no VirtualPath
        };

        var act = () => OctopusIisConfig.MapToKrakenIisConfig(config,
            PassThrough, fallbackWebRoot: @"C:\fallback");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*VirtualDirectory.VirtualPath*");
    }

    // ── Script-generation smoke tests for the new branches ────────────────

    [Fact]
    public void GenerateWebApplication_emits_New_WebApplication_call()
    {
        var cfg = new KrakenIisWebApplicationConfig
        {
            ParentSiteName = "ParentSite",
            VirtualPath = "/sub",
            PhysicalPath = @"C:\Apps\Sub",
            AppPool = new KrakenIisAppPool { Name = "SubPool", RuntimeVersion = "v4.0" },
        };

        var script = IisScriptGenerator.GenerateWebApplication(cfg, @"C:\extract", Guid.NewGuid());

        script.Should().Contain("$siteName     = 'ParentSite'");
        script.Should().Contain("$virtualPath  = '/sub'");
        script.Should().Contain("$physicalPath = 'C:\\Apps\\Sub'");
        script.Should().Contain("$appPoolName  = 'SubPool'");
        script.Should().Contain("New-WebApplication -Site $siteName");
        // The script references $siteName as a PowerShell variable — substitution
        // happens at run time, not generation time.
        script.Should().Contain("Parent site '$siteName' does not exist",
            "the script must guard against missing parent sites");
    }

    [Fact]
    public void GenerateWebApplication_normalises_leading_slash_on_virtualPath()
    {
        // Input "sub" (no slash) — expect "/sub" in the emitted script.
        var cfg = new KrakenIisWebApplicationConfig
        {
            ParentSiteName = "ParentSite",
            VirtualPath = "sub",
            PhysicalPath = @"C:\Apps\Sub",
            AppPool = new KrakenIisAppPool { Name = "P", RuntimeVersion = "v4.0" },
        };

        var script = IisScriptGenerator.GenerateWebApplication(cfg, @"C:\extract", Guid.NewGuid());
        script.Should().Contain("$virtualPath  = '/sub'");
    }

    [Fact]
    public void GenerateVirtualDirectory_emits_New_WebVirtualDirectory_call()
    {
        var cfg = new KrakenIisVirtualDirectoryConfig
        {
            ParentSiteName = "ParentSite",
            VirtualPath = "/static-content",
            PhysicalPath = @"C:\static",
        };

        var script = IisScriptGenerator.GenerateVirtualDirectory(cfg, @"C:\extract", Guid.NewGuid());

        script.Should().Contain("$siteName     = 'ParentSite'");
        script.Should().Contain("$virtualPath  = '/static-content'");
        script.Should().Contain("$physicalPath = 'C:\\static'");
        script.Should().Contain("New-WebVirtualDirectory -Site $siteName");
        // The script references $siteName as a PowerShell variable — substitution
        // happens at run time, not generation time.
        script.Should().Contain("Parent site '$siteName' does not exist",
            "the script must guard against missing parent sites");
        // Virtual directories don't have their own app pool — make sure we don't emit one.
        script.Should().NotContain("New-WebAppPool");
    }

    [Fact]
    public void MapToKrakenIisConfig_throws_when_WebSiteName_missing()
    {
        var config = new Dictionary<string, string>
        {
            [OctopusIisConfigKeys.DeploymentType] = "webSite",
            // no WebSiteName
        };

        var act = () => OctopusIisConfig.MapToKrakenIisConfig(config,
            octostache: PassThrough, fallbackWebRoot: @"C:\fallback");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*WebSiteName*");
    }

    // ── Helper ─────────────────────────────────────────────────────────────

    private static readonly Func<string, string> PassThrough = s => s;
}
