using System.Security.Cryptography;
using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Notifications;
using KrakenDeploy.Server.Data.Encryption;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Integration tests for M13.B.1 — <see cref="SmtpSettingsService"/>.
/// Exercises the singleton-row CRUD + the preserve/clear/rotate
/// password semantics; the SendProbe path needs a real SMTP server so
/// it's covered only by a connect-failure test (probe should never
/// throw — it returns a result with Succeeded=false).
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class SmtpSettingsServiceTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly string Base64Key = Convert.ToBase64String(
        RandomNumberGenerator.GetBytes(32));

    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateContext();
        await db.SmtpSettings.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GetAsync_returns_null_when_no_row()
    {
        var svc = NewSvc();
        (await svc.GetAsync()).Should().BeNull();
    }

    [Fact]
    public async Task UpsertAsync_creates_row_with_singleton_id_on_first_save()
    {
        var svc = NewSvc();

        var saved = await svc.UpsertAsync(SampleSettings(), newPassword: "hunter2");

        saved.Id.Should().Be(SmtpSettings.SingletonId,
            "the table is single-row — every save targets the fixed singleton id");
        saved.PasswordEncrypted.Should().BeNull(
            "the return value strips the cipher so callers can hand it " +
            "straight back to the UI");
    }

    [Fact]
    public async Task UpsertAsync_encrypts_password_at_rest()
    {
        var svc = NewSvc();
        await svc.UpsertAsync(SampleSettings(), newPassword: "hunter2");

        // Read raw from the DB — bypass the service's strip-the-cipher
        // logic to confirm it actually went through encryption.
        await using var db = postgres.CreateContext();
        var raw = await db.SmtpSettings.FirstAsync();

        raw.PasswordEncrypted.Should().NotBeNullOrEmpty();
        raw.PasswordEncrypted.Should().NotBe("hunter2",
            "plaintext password must not hit the column");
    }

    [Fact]
    public async Task GetDecryptedSettingsAsync_round_trips_password()
    {
        var svc = NewSvc();
        await svc.UpsertAsync(SampleSettings(), newPassword: "hunter2");

        var decrypted = await svc.GetDecryptedSettingsAsync();

        decrypted.Should().NotBeNull();
        decrypted!.Value.Password.Should().Be("hunter2",
            "the internal send-path overload must produce cleartext");
        decrypted.Value.Settings.PasswordEncrypted.Should().BeNull(
            "even the internal overload strips the cipher from the entity " +
            "so it never escapes the call site");
    }

    [Fact]
    public async Task Password_null_on_upsert_preserves_existing()
    {
        // The UI uses this contract: leaving the password field blank
        // means "keep whatever's in the DB". If a refactor changes this
        // to "clear when null", every operator who edits the host field
        // would wipe their SMTP auth.
        var svc = NewSvc();
        await svc.UpsertAsync(SampleSettings(), newPassword: "original");

        var edited = SampleSettings();
        edited.Host = "new.host.example.com";
        await svc.UpsertAsync(edited, newPassword: null);

        var decrypted = await svc.GetDecryptedSettingsAsync();
        decrypted!.Value.Password.Should().Be("original",
            "null password input means 'leave the existing one alone'");
        decrypted.Value.Settings.Host.Should().Be("new.host.example.com");
    }

    [Fact]
    public async Task Password_empty_on_upsert_clears_existing()
    {
        // Explicit empty-string is "clear it" — the operator switched
        // from authenticated to anonymous relay.
        var svc = NewSvc();
        await svc.UpsertAsync(SampleSettings(), newPassword: "original");
        await svc.UpsertAsync(SampleSettings(), newPassword: "");

        var decrypted = await svc.GetDecryptedSettingsAsync();
        decrypted!.Value.Password.Should().BeNull(
            "empty-string is the explicit 'clear' signal");

        await using var db = postgres.CreateContext();
        var raw = await db.SmtpSettings.FirstAsync();
        raw.PasswordEncrypted.Should().BeNull();
    }

    [Fact]
    public async Task Password_non_empty_on_upsert_replaces_existing()
    {
        var svc = NewSvc();
        await svc.UpsertAsync(SampleSettings(), newPassword: "old");
        await svc.UpsertAsync(SampleSettings(), newPassword: "new");

        var decrypted = await svc.GetDecryptedSettingsAsync();
        decrypted!.Value.Password.Should().Be("new");
    }

    [Fact]
    public async Task GetAsync_never_returns_cipher_to_caller()
    {
        var svc = NewSvc();
        await svc.UpsertAsync(SampleSettings(), newPassword: "hunter2");

        var fromGet = await svc.GetAsync();

        fromGet!.PasswordEncrypted.Should().BeNull(
            "GetAsync is the public surface that flows to the UI — the " +
            "cipher must NEVER cross to the browser, even encrypted, " +
            "because PasswordEncrypted serialises to JSON by default");
    }

    [Fact]
    public async Task UpsertAsync_trims_whitespace_from_strings()
    {
        var svc = NewSvc();
        var input = SampleSettings();
        input.Host            = "  smtp.example.com  ";
        input.Username        = "  bob  ";
        input.FromAddress     = "  bob@example.com  ";
        input.FromDisplayName = "  Bot  ";

        var saved = await svc.UpsertAsync(input, newPassword: null);

        saved.Host.Should().Be("smtp.example.com");
        saved.Username.Should().Be("bob");
        saved.FromAddress.Should().Be("bob@example.com");
        saved.FromDisplayName.Should().Be("Bot");
    }

    [Fact]
    public async Task Whitespace_only_username_becomes_null()
    {
        // "Anonymous auth" is signalled by Username == null. A whitespace-
        // only paste should not be treated as a username — that would put
        // the auth flow into "authenticate with empty user" which most
        // relays reject confusingly.
        var svc = NewSvc();
        var input = SampleSettings();
        input.Username = "   ";

        var saved = await svc.UpsertAsync(input, newPassword: null);

        saved.Username.Should().BeNull();
    }

    [Fact]
    public async Task Timeout_zero_or_negative_normalises_to_default()
    {
        var svc = NewSvc();
        var input = SampleSettings();
        input.TimeoutSeconds = 0;

        var saved = await svc.UpsertAsync(input, newPassword: null);

        saved.TimeoutSeconds.Should().Be(30,
            "a zero / negative timeout from a bad form binding would mean " +
            "MailKit fails instantly with no chance to connect — coerce " +
            "to the documented default instead of trusting bad input");
    }

    [Fact]
    public async Task SendProbeAsync_returns_failure_result_when_host_unreachable()
    {
        // Probe should NEVER throw — operators rely on the result detail
        // to see what went wrong. Point it at a deliberately bad host
        // (TEST-NET-1, RFC 5737 reserved for documentation) to provoke a
        // connect failure with no real DNS or service in the way.
        var svc = NewSvc();
        var settings = new SmtpSettings
        {
            Host           = "192.0.2.1", // RFC 5737 TEST-NET-1
            Port           = 25,
            TlsMode        = SmtpTlsMode.None,
            FromAddress    = "ops@example.com",
            TimeoutSeconds = 2, // keep the test fast
        };

        var result = await svc.SendProbeAsync(settings, passwordOverride: null,
            recipient: "alerts@example.com");

        result.Succeeded.Should().BeFalse(
            "an unreachable host must produce a failure result, not " +
            "propagate the connect exception to the caller");
        result.Detail.Should().NotBeNullOrEmpty(
            "the operator-facing message must say WHY it failed");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private SmtpSettingsService NewSvc() =>
        new(postgres,
            TestCrypto.Service(Base64Key),
            NullLogger<SmtpSettingsService>.Instance);

    private static SmtpSettings SampleSettings() => new()
    {
        Enabled         = true,
        Host            = "smtp.example.com",
        Port            = 587,
        TlsMode         = SmtpTlsMode.StartTlsRequired,
        Username        = "bob",
        FromAddress     = "bob@example.com",
        FromDisplayName = "Bob",
        TimeoutSeconds  = 30,
    };
}
