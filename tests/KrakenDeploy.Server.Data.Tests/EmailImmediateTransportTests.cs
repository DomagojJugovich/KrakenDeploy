using System.Security.Cryptography;
using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Notifications;
using KrakenDeploy.Server.Core.Domain.Subscriptions;
using KrakenDeploy.Server.Data.Encryption;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Data.Services.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Tests for <see cref="EmailImmediateTransport"/>'s validation +
/// guard-rail surface. The actual MailKit handshake is covered by
/// M13.B.1's SmtpSettingsService tests (which point at RFC 5737
/// TEST-NET-1 for deterministic failure); this file pins the routing
/// + config-validation contract so a wrong-shape subscription never
/// reaches the SMTP layer.
/// </summary>
[Collection("Postgres")]
public sealed class EmailImmediateTransportTests(PostgresFixture postgres)
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
    public async Task Digest_mode_subscription_is_rejected_by_immediate_transport()
    {
        // Routing contract: digest subscriptions go through the digest
        // flusher, not the immediate transport. The dispatcher should
        // never have called us; if it does, fail loudly.
        var transport = new EmailImmediateTransport(
            NewSmtpService(), NullLogger<EmailImmediateTransport>.Instance);

        var sub = new EventSubscription
        {
            Name                = "digest",
            SpaceId             = WellKnown.DefaultSpaceId,
            Transport           = SubscriptionTransport.Email,
            DigestEveryMinutes  = 15,
            TransportConfigJson = """{"recipients":["ops@example.com"]}""",
        };

        var result = await transport.DeliverAsync(sub, NewEvent(), default);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("digest mode",
            "digest subscriptions belong to the flusher; the immediate " +
            "transport refusing them is a defensive guard against routing bugs");
    }

    [Fact]
    public async Task Missing_SMTP_settings_yields_actionable_failure()
    {
        // Fresh fixture — no smtp_settings row.
        var transport = new EmailImmediateTransport(
            NewSmtpService(), NullLogger<EmailImmediateTransport>.Instance);

        var result = await transport.DeliverAsync(ImmediateSub(), NewEvent(), default);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("SMTP is not configured")
            .And.Contain("Configuration → SMTP",
                "the error points the operator at the page that fixes it");
    }

    [Fact]
    public async Task Disabled_SMTP_master_switch_yields_actionable_failure()
    {
        // Save settings but flip Enabled off.
        var smtp = NewSmtpService();
        await smtp.UpsertAsync(new SmtpSettings
        {
            Enabled         = false,
            Host            = "smtp.example.com",
            Port            = 587,
            TlsMode         = SmtpTlsMode.StartTlsRequired,
            FromAddress     = "kraken@example.com",
            TimeoutSeconds  = 30,
        }, newPassword: null);

        var transport = new EmailImmediateTransport(
            smtp, NullLogger<EmailImmediateTransport>.Instance);

        var result = await transport.DeliverAsync(ImmediateSub(), NewEvent(), default);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("master switch is off",
            "operator who paused outbound mail in the SMTP page needs to " +
            "see WHY their subscription stopped firing");
    }

    [Fact]
    public async Task Empty_recipients_yields_failure()
    {
        var smtp = NewSmtpService();
        await smtp.UpsertAsync(EnabledSmtp(), newPassword: null);

        var transport = new EmailImmediateTransport(
            smtp, NullLogger<EmailImmediateTransport>.Instance);

        var sub = new EventSubscription
        {
            Name                = "no-recipients",
            SpaceId             = WellKnown.DefaultSpaceId,
            Transport           = SubscriptionTransport.Email,
            TransportConfigJson = """{"recipients":[]}""",
        };

        var result = await transport.DeliverAsync(sub, NewEvent(), default);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("recipient");
    }

    [Fact]
    public async Task Bad_recipients_address_yields_failure_not_exception()
    {
        // MailKit's MailboxAddress.Parse throws ParseException on
        // malformed addresses. The transport must catch + report.
        var smtp = NewSmtpService();
        await smtp.UpsertAsync(EnabledSmtp(), newPassword: null);

        var transport = new EmailImmediateTransport(
            smtp, NullLogger<EmailImmediateTransport>.Instance);

        var sub = new EventSubscription
        {
            Name                = "bad-recipient",
            SpaceId             = WellKnown.DefaultSpaceId,
            Transport           = SubscriptionTransport.Email,
            TransportConfigJson = """{"recipients":["not-an-email-address"]}""",
        };

        var result = await transport.DeliverAsync(sub, NewEvent(), default);

        result.Succeeded.Should().BeFalse(
            "malformed address must produce a result, not propagate the " +
            "ParseException to the dispatcher");
    }

    [Fact]
    public async Task Unreachable_SMTP_host_yields_failure_with_verbatim_MailKit_message()
    {
        // RFC 5737 TEST-NET-1 — guaranteed unreachable. Same pattern as
        // SmtpSettingsService's own probe test. Confirms the MailKit
        // exception propagates to the result.Error verbatim.
        var smtp = NewSmtpService();
        await smtp.UpsertAsync(new SmtpSettings
        {
            Enabled         = true,
            Host            = "192.0.2.1", // TEST-NET-1
            Port            = 25,
            TlsMode         = SmtpTlsMode.None,
            FromAddress     = "kraken@example.com",
            TimeoutSeconds  = 2,
        }, newPassword: null);

        var transport = new EmailImmediateTransport(
            smtp, NullLogger<EmailImmediateTransport>.Instance);

        var result = await transport.DeliverAsync(ImmediateSub(), NewEvent(), default);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace(
            "MailKit's error message must surface — operators recognise " +
            "'Could not resolve host' / 'Connection timed out' etc.");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private SmtpSettingsService NewSmtpService() =>
        new(postgres,
            new AesEncryptionService(Base64Key),
            NullLogger<SmtpSettingsService>.Instance);

    private static SmtpSettings EnabledSmtp() => new()
    {
        Enabled         = true,
        Host            = "smtp.example.com",
        Port            = 587,
        TlsMode         = SmtpTlsMode.StartTlsRequired,
        FromAddress     = "kraken@example.com",
        TimeoutSeconds  = 30,
    };

    private static EventSubscription ImmediateSub() => new()
    {
        Name                = "immediate",
        SpaceId             = WellKnown.DefaultSpaceId,
        Transport           = SubscriptionTransport.Email,
        DigestEveryMinutes  = 0, // immediate mode
        TransportConfigJson = """{"recipients":["ops@example.com"]}""",
    };

    private static AuditEntry NewEvent() => new()
    {
        EventType   = "Deployment.Failed",
        OccurredUtc = DateTimeOffset.UtcNow,
        UserDisplay = "test",
        SpaceId     = WellKnown.DefaultSpaceId,
        Details     = "build #42 failed",
    };
}
