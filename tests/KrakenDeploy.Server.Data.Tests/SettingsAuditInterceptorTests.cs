using System.Text.Json;
using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Ai;
using KrakenDeploy.Server.Core.Domain.Settings;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Interceptors;
using KrakenDeploy.Server.Data.Settings;
using KrakenDeploy.Server.Data.Spaces;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Verifies the AuditLogInterceptor behavior specific to the unified `settings`
/// table (the shared PostgresFixture context intentionally omits the audit
/// interceptor, so this test builds its own context with it wired in):
///   1. secrets inside a settings document's jsonb payload are scrubbed out of
///      the audit snapshot (the name-based redaction list can't reach nested keys);
///   2. a Space-scoped settings document's audit row is attributed to its Space
///      via ScopeId (the entity has no "SpaceId" property).
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class SettingsAuditInterceptorTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateContext();
        await db.Set<Setting>().ExecuteDeleteAsync();
        await db.AuditEntries.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class NullHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private KrakenDbContext ContextWithAudit()
    {
        var spaceContext = new DefaultSpaceContext();
        var options = new DbContextOptionsBuilder<KrakenDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(
                new AuditableEntityInterceptor(TimeProvider.System),
                new AuditLogInterceptor(new NullHttpContextAccessor(), TimeProvider.System),
                new SpaceScopingInterceptor(spaceContext))
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new KrakenDbContext(options, spaceContext);
    }

    [Fact]
    public async Task Secret_bearing_settings_payload_is_scrubbed_from_the_audit_snapshot()
    {
        var spaceId = Guid.NewGuid();
        const string ciphertext = "SECRET-CIPHERTEXT-VALUE";
        var payload = JsonSerializer.Serialize(
            new SpaceAiSettings { Provider = "Anthropic", Model = "m", ApiKeyEncrypted = ciphertext },
            SettingsDocumentCatalog.JsonOptions);

        await using (var db = ContextWithAudit())
        {
            db.Set<Setting>().Add(new Setting
            {
                ScopeType = SettingsScope.Space,
                ScopeId = spaceId,
                Key = SpaceAiSettings.Key,
                Payload = payload,
            });
            await db.SaveChangesAsync();
        }

        await using var check = postgres.CreateContext();
        var audit = await check.AuditEntries.IgnoreQueryFilters()
            .SingleAsync(a => a.SubjectType == "Setting");

        // Ciphertext must NOT appear anywhere in the audit row.
        (audit.AfterJson ?? "").Should().NotContain(ciphertext,
            "the encrypted API key must be scrubbed from the audit payload");
        // Non-secret fields are still recorded (scrub nulls only *Encrypted members).
        audit.AfterJson.Should().Contain("Anthropic");
        // Distinguishable by key, attributed to its Space.
        audit.SubjectName.Should().Be(SpaceAiSettings.Key);
        audit.SpaceId.Should().Be(spaceId, "Space-scoped settings audit is stamped from ScopeId");
    }

    [Fact]
    public async Task System_settings_without_secrets_keep_their_full_payload_in_audit()
    {
        var payload = JsonSerializer.Serialize(
            new KrakenDeploy.Server.Core.Domain.Performance.PerformanceSettings { HangfireWorkerCount = 9 },
            SettingsDocumentCatalog.JsonOptions);

        await using (var db = ContextWithAudit())
        {
            db.Set<Setting>().Add(new Setting
            {
                ScopeType = SettingsScope.System,
                ScopeId = null,
                Key = KrakenDeploy.Server.Core.Domain.Performance.PerformanceSettings.Key,
                Payload = payload,
            });
            await db.SaveChangesAsync();
        }

        await using var check = postgres.CreateContext();
        var audit = await check.AuditEntries.IgnoreQueryFilters()
            .SingleAsync(a => a.SubjectType == "Setting");

        audit.SubjectName.Should().Be("performance");
        audit.SpaceId.Should().BeNull("System settings are not attributed to a Space");
        audit.AfterJson.Should().Contain("9", "a document with no secrets keeps its full payload");
    }
}
