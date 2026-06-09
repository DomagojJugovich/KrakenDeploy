using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Ai;
using KrakenDeploy.Server.Core.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Persistence round-trip tests for the M11.E.12 aggregates
/// (<see cref="AdhocSession"/> + <see cref="AdhocIteration"/>): jsonb
/// columns, int-backed enums, the cascade relationship, the unique
/// <c>(session_id, iter_number)</c> index, and Space auto-stamping +
/// query-filter scoping.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class AdhocSessionPersistenceTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateContext();
        await db.AdhocSessions.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Session_with_iterations_roundtrips_with_enums_jsonb_and_cascade()
    {
        var targetA = Guid.CreateVersion7();
        var targetB = Guid.CreateVersion7();

        Guid sessionId;
        await using (var db = postgres.CreateContext())
        {
            var session = new AdhocSession
            {
                Prompt              = "Check free disk space on the web tier",
                Mode                = AdhocMode.Readonly,
                FrozenTargetSetJson = $"[\"{targetA}\",\"{targetB}\"]",
                Status              = AdhocSessionStatus.Active,
                CreatedByUserId     = Guid.CreateVersion7(),
                CreatedByDisplay    = "ops@laus.hr",
                MaxIterations       = 5,
                Iterations =
                {
                    new AdhocIteration
                    {
                        IterNumber          = 1,
                        CreatedUtc          = DateTimeOffset.UtcNow,
                        GeneratedScript     = "Get-PSDrive C",
                        Description         = "Reads the C: drive free space",
                        RiskAssessment      = "None — read-only",
                        ExpectedOutputShape = "PSDrive object",
                        RequiresMutation    = false,
                        Status              = AdhocIterationStatus.Completed,
                        Verdict             = AdhocVerdict.AllSucceeded,
                        ResultsJson         = "[{\"targetId\":\"x\",\"exitCode\":0}]",
                        LlmModel            = "Anthropic/claude-sonnet-4.6",
                        LlmPromptTokens     = 120,
                        LlmCompletionTokens = 40,
                    },
                },
            };
            db.AdhocSessions.Add(session);
            await db.SaveChangesAsync();
            sessionId = session.Id;

            // ISpaceScoped → interceptor stamps the default Space.
            session.SpaceId.Should().Be(WellKnown.DefaultSpaceId);
        }

        await using (var db = postgres.CreateContext())
        {
            var loaded = await db.AdhocSessions
                .Include(s => s.Iterations)
                .AsNoTracking()
                .SingleAsync(s => s.Id == sessionId);

            loaded.Mode.Should().Be(AdhocMode.Readonly);
            loaded.Status.Should().Be(AdhocSessionStatus.Active);
            loaded.FrozenTargetSetJson.Should().Contain(targetA.ToString());
            loaded.Iterations.Should().ContainSingle();
            loaded.Iterations[0].Verdict.Should().Be(AdhocVerdict.AllSucceeded);
            loaded.Iterations[0].Status.Should().Be(AdhocIterationStatus.Completed);
            loaded.Iterations[0].GeneratedScript.Should().Be("Get-PSDrive C");
        }

        // Cascade: deleting the session removes its iterations.
        await using (var db = postgres.CreateContext())
        {
            var session = await db.AdhocSessions.SingleAsync(s => s.Id == sessionId);
            db.AdhocSessions.Remove(session);
            await db.SaveChangesAsync();

            (await db.AdhocIterations.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        }
    }

    [Fact]
    public async Task Duplicate_iter_number_within_a_session_is_rejected()
    {
        await using var db = postgres.CreateContext();
        var session = new AdhocSession
        {
            Prompt              = "dup test",
            FrozenTargetSetJson = "[]",
            CreatedByDisplay    = "ops@laus.hr",
            Iterations =
            {
                new AdhocIteration { IterNumber = 1, CreatedUtc = DateTimeOffset.UtcNow, GeneratedScript = "Get-Date" },
                new AdhocIteration { IterNumber = 1, CreatedUtc = DateTimeOffset.UtcNow, GeneratedScript = "Get-Date" },
            },
        };
        db.AdhocSessions.Add(session);

        var act = async () => await db.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }
}
