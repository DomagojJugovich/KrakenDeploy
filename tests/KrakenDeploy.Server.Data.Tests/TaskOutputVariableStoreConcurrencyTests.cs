using FluentAssertions;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// E-B item 5 — <see cref="TaskOutputVariableStore.UpsertAsync"/> is the single
/// capture path shared by <c>AgentHub.ReportStepCompletedAsync</c> and the
/// server-wave fold. The old read-then-insert let two concurrent callers for the
/// same (task, step, name) — an at-least-once duplicate step report racing the
/// original, or two parallel-wave targets sharing a step name — both miss the
/// read and both INSERT, throwing a unique-violation <c>DbUpdateException</c> out
/// of the hub. The PostgreSQL ON CONFLICT upsert must be race-free while
/// preserving the T0-6 encrypt-at-rest and in-place-overwrite rules.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class TaskOutputVariableStoreConcurrencyTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    // Fixed 32-byte AES-256 key — tests just want "encrypt with this key".
    private static readonly string Key = Convert.ToBase64String(new byte[32]);

    private async Task<(Guid TaskId, Guid SpaceId)> SeedTaskAsync()
    {
        var harness = new OrchestratorTestHarness(postgres);
        await using var _ = harness;
        var project = await harness.SeedProjectAsync($"p-{Guid.NewGuid():N}"[..16]);
        var env = await harness.SeedEnvironmentAsync($"e-{Guid.NewGuid():N}"[..16]);
        var targets = await harness.SeedTargetsAsync("t1");
        var release = await harness.SeedReleaseAsync(project.Id, "1.0", StepBuilder.Script("s1"));
        var taskId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);

        await using var db = postgres.CreateContext();
        var spaceId = await db.ServerTasks.IgnoreQueryFilters()
            .Where(t => t.Id == taskId).Select(t => t.SpaceId).SingleAsync();
        return (taskId, spaceId);
    }

    [Fact]
    public async Task Concurrent_upserts_for_the_same_key_do_not_throw()
    {
        var (taskId, spaceId) = await SeedTaskAsync();
        var encryption = TestCrypto.Service(Key);
        const string stepName = "capture-step";
        const string varName = "SharedVar";

        // N concurrent callers, each with its OWN context (mirrors AgentHub
        // creating a fresh context per hub call, and two parallel-wave targets
        // racing). A barrier maximises the read-then-insert overlap the old code
        // could not survive.
        const int concurrency = 8;
        using var barrier = new Barrier(concurrency);
        var tasks = Enumerable.Range(0, concurrency).Select(i => Task.Run(async () =>
        {
            await using var db = postgres.CreateContext();
            barrier.SignalAndWait();
            await TaskOutputVariableStore.UpsertAsync(
                db, taskId, spaceId, stepName,
                new Dictionary<string, string> { [varName] = $"value-{i}" },
                sensitiveNames: null, DateTimeOffset.UtcNow, encryption);
        })).ToArray();

        var act = () => Task.WhenAll(tasks);
        await act.Should().NotThrowAsync(
            "ON CONFLICT (task_id, step_name, name) DO UPDATE makes concurrent upserts race-free");

        // Exactly one row survives; its value came from one of the callers.
        await using var check = postgres.CreateContext();
        var rows = await check.TaskOutputVariables.IgnoreQueryFilters()
            .Where(o => o.TaskId == taskId && o.StepName == stepName && o.Name == varName)
            .ToListAsync();
        rows.Should().ContainSingle();
        rows[0].Value.Should().StartWith("value-");
        rows[0].IsSensitive.Should().BeFalse();
    }

    [Fact]
    public async Task Sensitive_value_is_stored_encrypted_and_reupsert_overwrites_in_place()
    {
        var (taskId, spaceId) = await SeedTaskAsync();
        var encryption = TestCrypto.Service(Key);
        const string stepName = "capture-step";
        const string varName = "Secret";

        await using (var db = postgres.CreateContext())
        {
            await TaskOutputVariableStore.UpsertAsync(
                db, taskId, spaceId, stepName,
                new Dictionary<string, string> { [varName] = "p@ss-1" },
                sensitiveNames: [varName], DateTimeOffset.UtcNow, encryption);
        }

        await using (var db = postgres.CreateContext())
        {
            var row = await db.TaskOutputVariables.IgnoreQueryFilters()
                .SingleAsync(o => o.TaskId == taskId && o.Name == varName);
            row.IsSensitive.Should().BeTrue();
            row.Value.Should().NotBe("p@ss-1", "a sensitive value is ciphertext at rest (T0-6)");
            encryption.Decrypt(row.Value).Should().Be("p@ss-1");
        }

        // Re-upsert the same key: overwrite in place, not append (ON CONFLICT UPDATE).
        await using (var db = postgres.CreateContext())
        {
            await TaskOutputVariableStore.UpsertAsync(
                db, taskId, spaceId, stepName,
                new Dictionary<string, string> { [varName] = "p@ss-2" },
                sensitiveNames: [varName], DateTimeOffset.UtcNow, encryption);
        }

        await using (var db = postgres.CreateContext())
        {
            var rows = await db.TaskOutputVariables.IgnoreQueryFilters()
                .Where(o => o.TaskId == taskId && o.Name == varName)
                .ToListAsync();
            rows.Should().ContainSingle("a re-upsert overwrites the existing row, never appends");
            encryption.Decrypt(rows[0].Value).Should().Be("p@ss-2");
        }
    }
}
