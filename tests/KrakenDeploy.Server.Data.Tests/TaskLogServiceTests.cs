using FluentAssertions;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Regression coverage for the hybrid task-log subsystem (fix 3): the DB-atomic
/// sequencer, the staging -> blob compactor, and the stitching reader. The
/// riskiest part is the blob serialization format
/// (<c>seq|iso8601|level|escaped-message</c>) — a message containing a pipe, a
/// newline, or a backslash must round-trip byte-for-byte through
/// <c>CompactStepAsync</c> + <c>ReadAllAsync</c>.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class TaskLogServiceTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly string[] TrickyMessages =
    [
        "plain line",
        "has a | pipe | in it",
        "has\nan embedded newline",
        "back\\slash and \\| an escaped-looking pipe",
        "trailing\r\ncrlf",
        "unicode check é and 中文",
    ];

    [Fact]
    public async Task Compaction_round_trips_messages_with_pipes_newlines_and_backslashes()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var taskId = await SeedTaskAsync(harness, "roundtrip");
        var ts = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);

        await using (var db = harness.CreateContext())
        {
            for (var i = 0; i < TrickyMessages.Length; i++)
            {
                var level = i switch { 1 => "warning", 3 => "error", _ => "info" };
                await TaskLogService.AppendLiveAsync(
                    db, taskId, stepIndex: 0, targetId: null, level, TrickyMessages[i], ts.AddSeconds(i));
            }
        }

        await using (var db = harness.CreateContext())
        {
            await TaskLogService.CompactStepAsync(
                db, taskId, stepIndex: 0, targetId: null, completedUtc: ts.AddMinutes(1));
        }

        await using (var db = harness.CreateContext())
        {
            // Staging is emptied; exactly one blob row with correct summary columns.
            (await db.TaskLogLive.CountAsync(l => l.TaskId == taskId))
                .Should().Be(0, "compaction moves staging lines into the blob and deletes them");
            var blobs = await db.TaskStepLogs.Where(b => b.TaskId == taskId).ToListAsync();
            blobs.Should().ContainSingle();
            blobs[0].LineCount.Should().Be(TrickyMessages.Length);
            blobs[0].ErrorCount.Should().Be(1);
            blobs[0].WarnCount.Should().Be(1);
            blobs[0].FirstErrorLine.Should().Be(3, "the error line is at sequence 3");

            // The stitched read returns the messages EXACTLY — escaping round-trips.
            var lines = await TaskLogService.ReadAllAsync(db, taskId);
            lines.Select(l => l.Message).Should().Equal(TrickyMessages);
            lines.Select(l => l.Sequence).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
            lines.Single(l => l.Sequence == 1).Level.Should().Be("warning");
        }
    }

    [Fact]
    public async Task ReadAll_stitches_completed_blobs_with_remaining_staging_in_order()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var taskId = await SeedTaskAsync(harness, "stitch");
        var ts = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);

        // Step 0 logs two lines, then completes (compacted to a blob).
        await using (var db = harness.CreateContext())
        {
            await TaskLogService.AppendLiveAsync(db, taskId, 0, null, "info", "step0-a", ts);
            await TaskLogService.AppendLiveAsync(db, taskId, 0, null, "info", "step0-b", ts.AddSeconds(1));
        }
        await using (var db = harness.CreateContext())
        {
            await TaskLogService.CompactStepAsync(db, taskId, 0, null, ts.AddSeconds(2));
        }

        // Step 1 logs two more lines that are still live (uncompacted).
        await using (var db = harness.CreateContext())
        {
            await TaskLogService.AppendLiveAsync(db, taskId, 1, null, "info", "step1-a", ts.AddSeconds(3));
            await TaskLogService.AppendLiveAsync(db, taskId, 1, null, "info", "step1-b", ts.AddSeconds(4));
        }

        await using (var db = harness.CreateContext())
        {
            var all = await TaskLogService.ReadAllAsync(db, taskId);
            all.Select(l => l.Message).Should().Equal("step0-a", "step0-b", "step1-a", "step1-b");

            // ReadSince skips everything up to and including the given sequence,
            // spanning the blob/staging boundary.
            var since = await TaskLogService.ReadSinceAsync(db, taskId, afterSequence: 1);
            since.Select(l => l.Message).Should().Equal("step1-a", "step1-b");
        }
    }

    // Minimal FK chain: TaskLogLiveEntry.TaskId is an enforced FK to server_tasks.
    private static async Task<Guid> SeedTaskAsync(OrchestratorTestHarness harness, string suffix)
    {
        // Unique names per test — the Postgres fixture is shared across the class.
        var project = await harness.SeedProjectAsync($"log-proj-{suffix}");
        var env = await harness.SeedEnvironmentAsync($"log-env-{suffix}");
        var targets = await harness.SeedTargetsAsync($"log-tgt-{suffix}");
        var release = await harness.SeedReleaseAsync(project.Id, "1.0.0");
        return await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
    }
}
