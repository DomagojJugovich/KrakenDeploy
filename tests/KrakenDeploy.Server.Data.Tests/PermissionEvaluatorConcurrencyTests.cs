using System.Security.Claims;
using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Data.Services;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Regression test for the intermittent first-load crash on pages that render
/// several <c>RequirePermission</c> checks (e.g. /packages):
///
///   InvalidOperationException: Operations that change non-concurrent
///   collections must have exclusive access. ... at Dictionary.TryInsert
///
/// <see cref="PermissionEvaluator"/> is a scoped service whose per-render memo
/// caches were plain <see cref="Dictionary{TKey,TValue}"/>. Blazor starts
/// sibling components' async lifecycle methods without a barrier, and the
/// evaluator's DB fills use ConfigureAwait(false), so the cache writes run on
/// thread-pool threads in parallel. On a cold circuit every check misses the
/// cache and writes at once → concurrent Dictionary.TryInsert → corruption.
///
/// This test reproduces that by hammering one evaluator instance with many
/// concurrent, cache-missing checks. With the plain-Dictionary caches it throws
/// intermittently; with ConcurrentDictionary it stays clean.
/// </summary>
[Collection("Postgres")]
public sealed class PermissionEvaluatorConcurrencyTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    private static ClaimsPrincipal User(Guid id) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, id.ToString())], authenticationType: "Test"));

    [Fact]
    public async Task Concurrent_cold_cache_checks_do_not_corrupt_the_caches()
    {
        // Distinct users force many distinct inserts into both caches, so the
        // backing dictionaries grow/rehash under contention — the condition
        // that reliably trips the non-concurrent-collection guard.
        const int users = 256;
        const int rounds = 8;
        var scope = new PermissionScope(SpaceId: Guid.NewGuid());

        for (var round = 0; round < rounds; round++)
        {
            // Fresh evaluator each round = cold caches, mirroring a new circuit.
            var evaluator = new PermissionEvaluator(postgres);
            var principals = Enumerable.Range(0, users).Select(_ => User(Guid.NewGuid()));

            var act = async () => await Parallel.ForEachAsync(
                principals,
                new ParallelOptions { MaxDegreeOfParallelism = 16 },
                async (user, ct) =>
                {
                    // Exercises both write paths: _systemAdminCache and
                    // _assignmentCache (the user has no teams → empty results,
                    // but the cache writes still happen — that's the race).
                    await evaluator.HasPermissionAsync(user, Permission.PackageView, scope, ct);
                });

            await act.Should().NotThrowAsync(
                "concurrent cold-cache permission checks must not corrupt the " +
                "evaluator's caches (round {0})", round);
        }
    }

    [Fact]
    public async Task Concurrent_checks_for_the_same_user_stay_consistent()
    {
        // The literal /packages shape: one user, the same permission evaluated
        // by several RequirePermission components at once on a cold circuit.
        var evaluator = new PermissionEvaluator(postgres);
        var user = User(Guid.NewGuid());
        var scope = new PermissionScope(SpaceId: Guid.NewGuid());

        var results = await Task.WhenAll(
            Enumerable.Range(0, 64).Select(_ =>
                evaluator.HasPermissionAsync(user, Permission.PackageView, scope)));

        // No teams/roles seeded → every concurrent check must agree on "false"
        // (and, more importantly, none may throw).
        results.Should().AllSatisfy(granted => granted.Should().BeFalse());
    }
}
