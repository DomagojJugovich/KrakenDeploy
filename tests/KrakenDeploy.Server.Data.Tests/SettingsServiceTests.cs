using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Ai;
using KrakenDeploy.Server.Core.Domain.Features;
using KrakenDeploy.Server.Core.Domain.Notifications;
using KrakenDeploy.Server.Core.Domain.Performance;
using KrakenDeploy.Server.Core.Domain.Settings;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Integration tests for <see cref="SettingsService"/> — the sole accessor for
/// the unified <c>settings</c> table. Covers round-trip, scoping, the single-row
/// invariant, enum-as-string serialization, and concurrency-safe read-modify-write.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class SettingsServiceTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateContext();
        await db.Set<Setting>().ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private SettingsService NewSvc() => new(postgres.ScopeFactory, TimeProvider.System);

    [Fact]
    public async Task System_document_round_trips_including_enum_as_string()
    {
        var svc = NewSvc();
        await svc.SaveAsync(new SmtpSettings
        {
            Enabled = true,
            Host = "smtp.laus.hr",
            Port = 465,
            TlsMode = SmtpTlsMode.ImplicitTls,
            FromAddress = "noreply@laus.hr",
            TimeoutSeconds = 45,
        });

        // Fresh service (cold cache) reads from the DB.
        var read = await NewSvc().GetAsync<SmtpSettings>();
        read.Enabled.Should().BeTrue();
        read.Host.Should().Be("smtp.laus.hr");
        read.Port.Should().Be(465);
        read.TlsMode.Should().Be(SmtpTlsMode.ImplicitTls);
        read.TimeoutSeconds.Should().Be(45);

        // The enum is stored as its NAME (JsonStringEnumConverter), not its ordinal
        // — a DEK rotation reserialize + any manual JSON inspection depend on it.
        // Parse rather than substring-match: Postgres jsonb normalises whitespace
        // and reorders keys, so the stored text is not byte-identical to the writer.
        await using var db = postgres.CreateContext();
        var payload = await db.Set<Setting>()
            .Where(s => s.Key == SmtpSettings.Key)
            .Select(s => s.Payload)
            .SingleAsync();
        using var json = System.Text.Json.JsonDocument.Parse(payload);
        var tlsMode = json.RootElement.GetProperty("tlsMode");
        tlsMode.ValueKind.Should().Be(System.Text.Json.JsonValueKind.String,
            "the enum must be persisted as its name, not its ordinal");
        tlsMode.GetString().Should().Be("ImplicitTls");
    }

    [Fact]
    public async Task GetAsync_returns_defaults_and_TryGet_returns_null_when_absent()
    {
        var svc = NewSvc();

        // GetAsync materialises defaults from the POCO's property initializers.
        (await svc.GetAsync<PerformanceSettings>()).HangfireWorkerCount
            .Should().Be(PerformanceSettings.DefaultHangfireWorkerCount);
        // TryGetAsync distinguishes "never saved" (null) from "saved defaults".
        (await svc.TryGetAsync<SmtpSettings>()).Should().BeNull();
    }

    [Fact]
    public async Task Saving_a_system_document_twice_keeps_a_single_row()
    {
        var svc = NewSvc();
        await svc.SaveAsync(new SmtpSettings { Host = "a", FromAddress = "a@x" });
        await svc.SaveAsync(new SmtpSettings { Host = "b", FromAddress = "b@x" });

        await using var db = postgres.CreateContext();
        var count = await db.Set<Setting>().CountAsync(s => s.Key == SmtpSettings.Key);
        count.Should().Be(1, "NULLS NOT DISTINCT + upsert keep one System row per key");

        (await NewSvc().GetAsync<SmtpSettings>()).Host.Should().Be("b");
    }

    [Fact]
    public async Task Space_documents_are_isolated_by_scope_id()
    {
        var spaceA = Guid.NewGuid();
        var spaceB = Guid.NewGuid();
        var svc = NewSvc();

        await svc.SaveAsync(new SpaceAiSettings { McpEnabled = true, Model = "a-model" }, spaceA);
        await svc.SaveAsync(new SpaceAiSettings { McpEnabled = false, Model = "b-model" }, spaceB);

        var readA = await NewSvc().GetAsync<SpaceAiSettings>(spaceA);
        var readB = await NewSvc().GetAsync<SpaceAiSettings>(spaceB);
        readA.McpEnabled.Should().BeTrue();
        readA.Model.Should().Be("a-model");
        readB.McpEnabled.Should().BeFalse();
        readB.Model.Should().Be("b-model");

        // A third, never-configured Space gets defaults, not a neighbour's document.
        (await NewSvc().GetAsync<SpaceAiSettings>(Guid.NewGuid())).Model.Should().BeNull();
    }

    [Fact]
    public async Task Space_document_without_scope_id_throws()
    {
        var svc = NewSvc();
        var act = async () => await svc.GetAsync<SpaceAiSettings>();
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Concurrent_feature_toggles_do_not_clobber_each_other()
    {
        // The feature-flag overrides are a single document; concurrent
        // read-modify-writes must all survive via xmin retry, not last-write-wins.
        var svc = NewSvc();
        var keys = Enumerable.Range(0, 4).Select(i => $"feature.k{i}").ToArray();

        await Task.WhenAll(keys.Select(k =>
            svc.MutateAsync<FeatureFlagsDocument>(scopeId: null, doc =>
            {
                doc.Overrides[k] = true;
                return doc;
            })));

        var final = await NewSvc().GetAsync<FeatureFlagsDocument>();
        final.Overrides.Should().ContainKeys(keys);
        final.Overrides.Should().HaveCount(keys.Length);
    }
}
