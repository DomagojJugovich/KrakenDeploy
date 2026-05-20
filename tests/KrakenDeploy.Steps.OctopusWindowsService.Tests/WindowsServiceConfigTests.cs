using FluentAssertions;
using KrakenDeploy.Steps.OctopusWindowsService;
using Octostache;

namespace KrakenDeploy.Steps.OctopusWindowsService.Tests;

/// <summary>
/// Unit tests for <see cref="WindowsServiceConfig.Parse"/> and
/// <see cref="WindowsServiceScriptGenerator.Generate"/>. Drives the parser
/// with hand-crafted Octopus property bags; the generator's output is
/// asserted by substring match (the script is a deterministic emit, so
/// fragment-level assertions are robust without snapshot tooling).
/// </summary>
public sealed class WindowsServiceConfigTests
{
    // ── Parse ─────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_minimum_required_fields_succeeds_with_sensible_defaults()
    {
        var config = new Dictionary<string, string>
        {
            [WindowsServiceConfigKeys.ServiceName]    = "MySvc",
            [WindowsServiceConfigKeys.ExecutablePath] = "bin\\MySvc.exe",
        };

        var cfg = WindowsServiceConfig.Parse(
            config, octostache: PassThrough, fallbackInstallRoot: @"C:\extract\xyz");

        cfg.ServiceName.Should().Be("MySvc");
        cfg.ExecutablePath.Should().Be("bin\\MySvc.exe");
        cfg.DisplayName.Should().Be("MySvc",       "DisplayName defaults to ServiceName when absent");
        cfg.StartMode.Should().Be("auto",          "StartMode defaults to auto");
        cfg.DesiredStatus.Should().Be("Running",   "DesiredStatus defaults to Running");
        cfg.ServiceAccount.Should().Be("LocalSystem");
        cfg.InstallRoot.Should().Be(@"C:\extract\xyz");
        cfg.Dependencies.Should().BeEmpty();
        cfg.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Parse_throws_when_ServiceName_missing()
    {
        var config = new Dictionary<string, string>
        {
            [WindowsServiceConfigKeys.ExecutablePath] = "bin\\MySvc.exe",
        };
        var act = () => WindowsServiceConfig.Parse(
            config, PassThrough, @"C:\fallback");

        act.Should().Throw<InvalidOperationException>().WithMessage("*ServiceName*");
    }

    [Fact]
    public void Parse_throws_when_ExecutablePath_missing()
    {
        var config = new Dictionary<string, string>
        {
            [WindowsServiceConfigKeys.ServiceName] = "MySvc",
        };
        var act = () => WindowsServiceConfig.Parse(
            config, PassThrough, @"C:\fallback");

        act.Should().Throw<InvalidOperationException>().WithMessage("*ExecutablePath*");
    }

    [Fact]
    public void Parse_octostache_evaluates_ServiceName_DisplayName_ExecutablePath_and_CustomInstallDir()
    {
        var config = new Dictionary<string, string>
        {
            [WindowsServiceConfigKeys.ServiceName]    = "Argosy_#{Octopus.Environment.Name}",
            [WindowsServiceConfigKeys.DisplayName]    = "Argosy (#{Octopus.Environment.Name})",
            [WindowsServiceConfigKeys.ExecutablePath] = "bin\\Argosy.exe",
            [WindowsServiceConfigKeys.CustomInstallationDirectory] =
                @"C:\Argosy\#{Octopus.Environment.Name}",
        };
        var vars = new VariableDictionary { ["Octopus.Environment.Name"] = "Production" };

        var cfg = WindowsServiceConfig.Parse(
            config, vars.Evaluate, fallbackInstallRoot: @"C:\fallback");

        cfg.ServiceName.Should().Be("Argosy_Production");
        cfg.DisplayName.Should().Be("Argosy (Production)");
        cfg.InstallRoot.Should().Be(@"C:\Argosy\Production");
    }

    [Theory]
    [InlineData("Automatic",            "auto")]
    [InlineData("auto",                 "auto")]
    [InlineData("Automatic (delayed)",  "delayed-auto")]
    [InlineData("delayed-auto",         "delayed-auto")]
    [InlineData("Manual",               "demand")]
    [InlineData("manual",               "demand")]
    [InlineData("Disabled",             "disabled")]
    [InlineData("Unchanged",            "unchanged")]
    public void Parse_normalises_StartMode_synonyms_to_canonical_tokens(string input, string expected)
    {
        var config = new Dictionary<string, string>
        {
            [WindowsServiceConfigKeys.ServiceName]    = "MySvc",
            [WindowsServiceConfigKeys.ExecutablePath] = "bin\\MySvc.exe",
            [WindowsServiceConfigKeys.StartMode]      = input,
        };
        var cfg = WindowsServiceConfig.Parse(config, PassThrough, @"C:\fallback");
        cfg.StartMode.Should().Be(expected);
    }

    [Fact]
    public void Parse_unknown_StartMode_warns_and_defaults_to_auto()
    {
        var config = new Dictionary<string, string>
        {
            [WindowsServiceConfigKeys.ServiceName]    = "MySvc",
            [WindowsServiceConfigKeys.ExecutablePath] = "bin\\MySvc.exe",
            [WindowsServiceConfigKeys.StartMode]      = "totally-bogus",
        };
        var cfg = WindowsServiceConfig.Parse(config, PassThrough, @"C:\fallback");
        cfg.StartMode.Should().Be("auto");
        cfg.Warnings.Should().Contain(w => w.Contains("totally-bogus"));
    }

    [Fact]
    public void Parse_custom_account_with_password_succeeds()
    {
        var config = new Dictionary<string, string>
        {
            [WindowsServiceConfigKeys.ServiceName]           = "MySvc",
            [WindowsServiceConfigKeys.ExecutablePath]        = "bin\\MySvc.exe",
            [WindowsServiceConfigKeys.ServiceAccount]        = "_CUSTOM",
            [WindowsServiceConfigKeys.CustomAccountName]     = "DOMAIN\\svc-account",
            [WindowsServiceConfigKeys.CustomAccountPassword] = "Sup3rs3cret",
        };
        var cfg = WindowsServiceConfig.Parse(config, PassThrough, @"C:\fallback");
        cfg.ServiceAccount.Should().Be("_CUSTOM");
        cfg.CustomAccountName.Should().Be("DOMAIN\\svc-account");
        cfg.CustomAccountPassword.Should().Be("Sup3rs3cret");
    }

    [Fact]
    public void Parse_custom_account_without_name_throws()
    {
        var config = new Dictionary<string, string>
        {
            [WindowsServiceConfigKeys.ServiceName]    = "MySvc",
            [WindowsServiceConfigKeys.ExecutablePath] = "bin\\MySvc.exe",
            [WindowsServiceConfigKeys.ServiceAccount] = "_CUSTOM",
            // no CustomAccountName
        };
        var act = () => WindowsServiceConfig.Parse(config, PassThrough, @"C:\fallback");
        act.Should().Throw<InvalidOperationException>().WithMessage("*CustomAccountName*");
    }

    [Fact]
    public void Parse_sensitive_password_envelope_emits_warning_and_drops_value()
    {
        // Octopus emits sensitive properties as {HasValue,NewValue,Hint} envelopes.
        // The B-2 importer preserves the envelope as JSON text; the parser detects
        // it and warns instead of trying to use it as a real password.
        var config = new Dictionary<string, string>
        {
            [WindowsServiceConfigKeys.ServiceName]           = "MySvc",
            [WindowsServiceConfigKeys.ExecutablePath]        = "bin\\MySvc.exe",
            [WindowsServiceConfigKeys.ServiceAccount]        = "_CUSTOM",
            [WindowsServiceConfigKeys.CustomAccountName]     = "DOMAIN\\svc",
            [WindowsServiceConfigKeys.CustomAccountPassword] =
                "{\"HasValue\":true,\"NewValue\":null,\"Hint\":null}",
        };

        var cfg = WindowsServiceConfig.Parse(config, PassThrough, @"C:\fallback");

        cfg.CustomAccountPassword.Should().BeNull();
        cfg.Warnings.Should().Contain(w => w.Contains("sensitive-value envelope"));
    }

    [Fact]
    public void Parse_dependencies_string_splits_on_slash_and_comma()
    {
        var config = new Dictionary<string, string>
        {
            [WindowsServiceConfigKeys.ServiceName]    = "MySvc",
            [WindowsServiceConfigKeys.ExecutablePath] = "bin\\MySvc.exe",
            [WindowsServiceConfigKeys.Dependencies]   = "LanmanWorkstation/TCPIP",
        };
        var cfg = WindowsServiceConfig.Parse(config, PassThrough, @"C:\fallback");
        cfg.Dependencies.Should().Equal("LanmanWorkstation", "TCPIP");
    }

    [Fact]
    public void Parse_falls_back_to_extractDir_when_no_CustomInstallationDirectory()
    {
        var config = new Dictionary<string, string>
        {
            [WindowsServiceConfigKeys.ServiceName]    = "MySvc",
            [WindowsServiceConfigKeys.ExecutablePath] = "bin\\MySvc.exe",
        };
        var cfg = WindowsServiceConfig.Parse(config, PassThrough, fallbackInstallRoot: @"C:\extract\abc");
        cfg.InstallRoot.Should().Be(@"C:\extract\abc");
    }

    // ── Script generation ────────────────────────────────────────────────

    [Fact]
    public void Generate_emits_stop_delete_recreate_pattern()
    {
        var cfg = MinimalConfig();
        var script = WindowsServiceScriptGenerator.Generate(cfg, Guid.NewGuid());

        script.Should().Contain("Stop-Service");
        script.Should().Contain("sc.exe delete");
        script.Should().Contain("sc.exe @scArgs");
        script.Should().Contain("'create', $serviceName");
    }

    [Fact]
    public void Generate_LocalSystem_account_writes_LocalSystem_obj()
    {
        var cfg = MinimalConfig() with { ServiceAccount = "LocalSystem" };
        var script = WindowsServiceScriptGenerator.Generate(cfg, Guid.NewGuid());
        script.Should().Contain("'obj=', 'LocalSystem'");
    }

    [Fact]
    public void Generate_LocalService_account_writes_NT_AUTHORITY_LocalService()
    {
        var cfg = MinimalConfig() with { ServiceAccount = "LocalService" };
        var script = WindowsServiceScriptGenerator.Generate(cfg, Guid.NewGuid());
        script.Should().Contain("'obj=', 'NT AUTHORITY\\LocalService'");
    }

    [Fact]
    public void Generate_NetworkService_account_writes_NT_AUTHORITY_NetworkService()
    {
        var cfg = MinimalConfig() with { ServiceAccount = "NetworkService" };
        var script = WindowsServiceScriptGenerator.Generate(cfg, Guid.NewGuid());
        script.Should().Contain("'obj=', 'NT AUTHORITY\\NetworkService'");
    }

    [Fact]
    public void Generate_CUSTOM_account_writes_username_and_password()
    {
        var cfg = MinimalConfig() with
        {
            ServiceAccount = "_CUSTOM",
            CustomAccountName = "DOMAIN\\svc",
            CustomAccountPassword = "p4ssw0rd",
        };
        var script = WindowsServiceScriptGenerator.Generate(cfg, Guid.NewGuid());
        script.Should().Contain("'obj=', 'DOMAIN\\svc'");
        script.Should().Contain("'password=', 'p4ssw0rd'");
    }

    [Fact]
    public void Generate_CUSTOM_account_without_password_omits_password_arg_for_MSA_support()
    {
        // Managed Service Accounts authenticate without a password (the SCM uses
        // their MSA secret) — Octopus docs explicitly call this out. So the
        // generated script must not pass an empty password= token.
        var cfg = MinimalConfig() with
        {
            ServiceAccount = "_CUSTOM",
            CustomAccountName = "DOMAIN\\msa$",
            CustomAccountPassword = null,
        };
        var script = WindowsServiceScriptGenerator.Generate(cfg, Guid.NewGuid());
        script.Should().Contain("'obj=', 'DOMAIN\\msa$'");
        script.Should().NotContain("password=");
    }

    [Fact]
    public void Generate_dependencies_join_with_slashes()
    {
        var cfg = MinimalConfig() with { Dependencies = new[] { "LanmanWorkstation", "TCPIP" } };
        var script = WindowsServiceScriptGenerator.Generate(cfg, Guid.NewGuid());
        script.Should().Contain("'depend=', 'LanmanWorkstation/TCPIP'");
    }

    [Fact]
    public void Generate_DesiredStatus_Running_emits_StartService_call()
    {
        var cfg = MinimalConfig() with { DesiredStatus = "Running" };
        var script = WindowsServiceScriptGenerator.Generate(cfg, Guid.NewGuid());
        script.Should().Contain("Start-Service -Name $serviceName");
    }

    [Fact]
    public void Generate_DesiredStatus_Stopped_does_not_start_the_service()
    {
        var cfg = MinimalConfig() with { DesiredStatus = "Stopped" };
        var script = WindowsServiceScriptGenerator.Generate(cfg, Guid.NewGuid());
        script.Should().NotContain("Start-Service -Name $serviceName");
        script.Should().Contain("DesiredStatus=Stopped");
    }

    [Theory]
    [InlineData("auto",          "'start=',   'auto'")]
    [InlineData("delayed-auto",  "'start=',   'delayed-auto'")]
    [InlineData("demand",        "'start=',   'demand'")]
    [InlineData("disabled",      "'start=',   'disabled'")]
    public void Generate_StartMode_maps_to_sc_exe_start_token(string mode, string expectedFragment)
    {
        var cfg = MinimalConfig() with { StartMode = mode };
        var script = WindowsServiceScriptGenerator.Generate(cfg, Guid.NewGuid());
        script.Should().Contain(expectedFragment);
    }

    [Fact]
    public void Generate_description_invokes_sc_exe_description()
    {
        var cfg = MinimalConfig() with { Description = "Argosy desktop service" };
        var script = WindowsServiceScriptGenerator.Generate(cfg, Guid.NewGuid());
        script.Should().Contain("sc.exe description $serviceName 'Argosy desktop service'");
    }

    [Fact]
    public void Generate_escapes_single_quotes_in_string_values()
    {
        // PowerShell single-quoted literals escape ' by doubling.
        var cfg = MinimalConfig() with
        {
            Description = "Don't break the script",
            ServiceAccount = "_CUSTOM",
            CustomAccountName = "DOMAIN\\O'Reilly",
            CustomAccountPassword = "pa'ss'",
        };
        var script = WindowsServiceScriptGenerator.Generate(cfg, Guid.NewGuid());
        script.Should().Contain("'Don''t break the script'");
        script.Should().Contain("'DOMAIN\\O''Reilly'");
        script.Should().Contain("'pa''ss'''");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static WindowsServiceConfig MinimalConfig() => new()
    {
        ServiceName    = "MySvc",
        DisplayName    = "MySvc",
        ExecutablePath = "bin\\MySvc.exe",
        StartMode      = "auto",
        DesiredStatus  = "Running",
        ServiceAccount = "LocalSystem",
        InstallRoot    = @"C:\install",
    };

    private static readonly Func<string, string> PassThrough = s => s;
}
