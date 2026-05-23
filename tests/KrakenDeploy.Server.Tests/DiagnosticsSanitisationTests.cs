using FluentAssertions;
using KrakenDeploy.Server.Services;
using Microsoft.Extensions.Configuration;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// Unit tests for the M13.A.2 diagnostics-zip secret-redaction contract.
/// The zip is intended to be safe to attach to a public support ticket;
/// any config value that smells of credentials MUST be redacted.
///
/// Two surfaces under test:
///   <see cref="DiagnosticsService.IsSensitiveKey"/> — key-name classifier
///   <see cref="DiagnosticsService.StripConnectionStringSecrets"/> — value-level redact
/// </summary>
public sealed class DiagnosticsSanitisationTests
{
    // ── IsSensitiveKey ─────────────────────────────────────────────────────

    [Theory]
    // Hits — known LAUS-context secret-bearing keys
    [InlineData("Encryption:MasterKey",                  true)]
    [InlineData("License:Key",                           true)]
    [InlineData("ApiKey:Key",                            true)]
    [InlineData("KRAKEN_LICENSE_KEY",                    true)]
    [InlineData("Smtp:Password",                         true)]
    [InlineData("OpenId:ClientSecret",                   true)]
    [InlineData("GitHub:Token",                          true)]
    [InlineData("Foo:SomethingApiKey",                   true)]
    [InlineData("Foo:SomethingAuthToken",                true)]
    [InlineData("Foo:PrivateKey",                        true)]
    [InlineData("Server:Smtp:Password",                  true)]
    // Misses — legitimate non-sensitive keys
    [InlineData("Server:DataPath",                       false)]
    [InlineData("Server:BaseUrl",                        false)]
    [InlineData("ConnectionStrings:KrakenDb",            false)] // value-level redact instead
    [InlineData("Hangfire:DashboardEnabled",             false)]
    [InlineData("Retention:AuditLogDays",                false)]
    [InlineData("Ai:Provider",                           false)]
    public void Classifier_matches_known_keys(string key, bool expectedSensitive)
    {
        DiagnosticsService.IsSensitiveKey(key).Should().Be(expectedSensitive,
            $"the classifier needs to either redact '{key}' or let it through, " +
            "and getting that wrong on either side is bad: false-positive bloats " +
            "the report with [REDACTED], false-negative leaks a secret to a " +
            "support ticket");
    }

    [Fact]
    public void Classifier_is_case_insensitive()
    {
        // Config keys arrive in whatever casing the source binder produced —
        // env vars are typically SCREAMING_CASE, JSON is usually PascalCase.
        DiagnosticsService.IsSensitiveKey("smtp:password").Should().BeTrue();
        DiagnosticsService.IsSensitiveKey("SMTP:PASSWORD").Should().BeTrue();
        DiagnosticsService.IsSensitiveKey("kraken_license_key").Should().BeTrue();
    }

    // ── StripConnectionStringSecrets ───────────────────────────────────────

    [Theory]
    [InlineData(
        "Host=localhost;Port=5432;Database=kraken;Username=krk;Password=hunter2",
        "Host=localhost;Port=5432;Database=kraken;Username=krk;Password=[REDACTED]")]
    [InlineData(
        "host=localhost;password=Some$Complex!Pwd;db=kraken",
        "host=localhost;password=[REDACTED];db=kraken")]
    // Different keyword Pwd
    [InlineData(
        "Server=db;Pwd=hunter2;Uid=krk",
        "Server=db;Pwd=[REDACTED];Uid=krk")]
    // Mixed case + spaces around = (regex's [^;]* eats trailing whitespace
    // before the next semicolon too — semantically identical to most
    // connection-string parsers, which trim around segments anyway).
    [InlineData(
        "host=localhost ; PassWord = hunter2 ; db=kraken",
        "host=localhost ; PassWord=[REDACTED]; db=kraken")]
    public void Strip_redacts_password_segment(string input, string expected)
    {
        DiagnosticsService.StripConnectionStringSecrets(input).Should().Be(expected);
    }

    [Fact]
    public void Strip_returns_null_or_empty_unchanged()
    {
        DiagnosticsService.StripConnectionStringSecrets(null).Should().BeNull();
        DiagnosticsService.StripConnectionStringSecrets("").Should().Be("");
    }

    [Fact]
    public void Strip_does_not_touch_password_free_connection_strings()
    {
        const string Cs = "Host=localhost;Port=5432;Database=kraken;Username=krk";
        DiagnosticsService.StripConnectionStringSecrets(Cs).Should().Be(Cs);
    }

    // ── SanitiseConfig end-to-end ──────────────────────────────────────────

    [Fact]
    public void Sanitise_redacts_sensitive_keys_in_full_config()
    {
        // Build a representative IConfiguration the way Program.cs does:
        // mixture of plain values, secret-shaped keys, and a connection
        // string that has its password embedded in the value.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Server:BaseUrl"]                = "https://kraken.example.com",
                ["Server:DataPath"]               = "/var/lib/kraken",
                ["ConnectionStrings:KrakenDb"]    = "Host=db;Database=kraken;Password=hunter2",
                ["Encryption:MasterKey"]          = "base64-32-bytes-here",
                ["License:Key"]                   = "eyJhbGc-jwt-blob",
                ["Smtp:Host"]                     = "smtp.example.com",
                ["Smtp:Password"]                 = "smtp-pass",
                ["GitHub:Token"]                  = "ghp_abc123",
                ["Ai:Provider"]                   = "Anthropic",
            })
            .Build();

        var sanitised = DiagnosticsService.SanitiseConfig(config);

        // Non-sensitive values come through unchanged.
        sanitised["Server:BaseUrl"].Should().Be("https://kraken.example.com");
        sanitised["Server:DataPath"].Should().Be("/var/lib/kraken");
        sanitised["Smtp:Host"].Should().Be("smtp.example.com");
        sanitised["Ai:Provider"].Should().Be("Anthropic");

        // Key-level redactions (the keys themselves match a sensitive-shape rule).
        sanitised["Encryption:MasterKey"].Should().Be("[REDACTED]");
        sanitised["License:Key"].Should().Be("[REDACTED]");
        sanitised["Smtp:Password"].Should().Be("[REDACTED]");
        sanitised["GitHub:Token"].Should().Be("[REDACTED]");

        // Connection string: value-level password strip; rest stays.
        sanitised["ConnectionStrings:KrakenDb"].Should().Contain("Host=db")
            .And.Contain("Database=kraken")
            .And.Contain("Password=[REDACTED]")
            .And.NotContain("hunter2");
    }

    [Fact]
    public void Sanitise_never_leaves_a_jwt_shaped_value_in_a_key_that_looks_sensitive()
    {
        // Belt-and-braces: even if a new sensitive-shaped key sneaks past
        // the classifier, a JWT-like value (three dot-separated base64
        // segments) is a strong signal we leaked something. This test
        // pins that the License:Key path is fully covered.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["License:Key"] = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ0ZXN0In0.signature-bytes",
            })
            .Build();

        var sanitised = DiagnosticsService.SanitiseConfig(config);

        sanitised["License:Key"].Should().NotContain(".",
            "two-or-more dots strongly suggests a JWT slipped through; " +
            "[REDACTED] has no dots and is the only acceptable value here");
    }
}
