using KrakenDeploy.Server.Core.Domain.Freezes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// CRUD + match-check for global deployment freezes (M13.F.2).
/// <see cref="FindBlockingFreezeAsync"/> is the hot path — it runs once
/// per deployment dispatch — so the implementation reads from a short-
/// TTL in-memory cache rather than hitting the DB every time.
///
/// <para>
/// Freezes are Space-scoped. The dispatcher passes the deployment's
/// owning Space ID; the cache key is the Space ID; per-Space freezes never
/// match across Spaces.
/// </para>
/// </summary>
public sealed class DeploymentFreezeService(
    IServiceScopeFactory scopeFactory,
    TimeProvider time)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Dictionary<Guid, (List<DeploymentFreeze> Freezes, DateTimeOffset At)> _cache = [];

    // ── CRUD ───────────────────────────────────────────────────────────────

    /// <summary>All freezes in the current Space (ambient scope).</summary>
    public async Task<List<DeploymentFreeze>> GetAllAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();
        return await db.DeploymentFreezes
            .OrderBy(f => f.StartUtc)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<DeploymentFreeze?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();
        return await db.DeploymentFreezes
            .FirstOrDefaultAsync(f => f.Id == id, ct)
            .ConfigureAwait(false);
    }

    public async Task<DeploymentFreeze> CreateAsync(DeploymentFreeze input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        Validate(input);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();
        db.DeploymentFreezes.Add(input);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        InvalidateCache();
        return input;
    }

    public async Task<DeploymentFreeze?> UpdateAsync(
        Guid id, DeploymentFreeze input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        Validate(input);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();
        var existing = await db.DeploymentFreezes
            .FirstOrDefaultAsync(f => f.Id == id, ct)
            .ConfigureAwait(false);
        if (existing is null) { return null; }

        existing.Name                    = input.Name;
        existing.Description             = input.Description;
        existing.StartUtc                = input.StartUtc;
        existing.EndUtc                  = input.EndUtc;
        existing.ProjectIds              = [.. input.ProjectIds];
        existing.EnvironmentIds          = [.. input.EnvironmentIds];
        existing.TenantTagCanonicalNames = [.. input.TenantTagCanonicalNames];
        existing.Disabled                = input.Disabled;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        InvalidateCache();
        return existing;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();
        var existing = await db.DeploymentFreezes
            .FirstOrDefaultAsync(f => f.Id == id, ct)
            .ConfigureAwait(false);
        if (existing is null) { return false; }

        db.DeploymentFreezes.Remove(existing);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        InvalidateCache();
        return true;
    }

    // ── Dispatch check ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns the first freeze that blocks the supplied deployment context,
    /// or <see langword="null"/> when no freeze is active. "First" is
    /// non-deterministic across multiple matches — the operator-facing
    /// error only names one, picked at random; the freezes page shows all
    /// so deduplication is the operator's job.
    /// </summary>
    /// <param name="spaceId">Deployment's owning Space.</param>
    /// <param name="projectId">Deployment's project.</param>
    /// <param name="environmentId">Deployment's target environment.</param>
    /// <param name="tenantTagCanonicalNames">Tags on the deployment's tenant
    /// (<c>tagSetName/tagName</c>, lowercase). Empty for untenanted deployments.</param>
    public async Task<DeploymentFreeze?> FindBlockingFreezeAsync(
        Guid spaceId,
        Guid projectId,
        Guid environmentId,
        IReadOnlyCollection<string>? tenantTagCanonicalNames = null,
        CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var freezes = await GetActiveFreezesForSpaceAsync(spaceId, ct).ConfigureAwait(false);
        var tags = tenantTagCanonicalNames ?? [];

        foreach (var freeze in freezes)
        {
            if (!freeze.IsActiveAt(now)) { continue; }

            // Empty scope-list = "applies to anything" for that dimension.
            // Non-empty = applies only when the deployment's value matches.
            if (freeze.ProjectIds.Count > 0 && !freeze.ProjectIds.Contains(projectId))
            {
                continue;
            }
            if (freeze.EnvironmentIds.Count > 0 && !freeze.EnvironmentIds.Contains(environmentId))
            {
                continue;
            }
            if (freeze.TenantTagCanonicalNames.Count > 0 &&
                !freeze.TenantTagCanonicalNames.Any(t => tags.Contains(t, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            return freeze;
        }

        return null;
    }

    // ── Cache ──────────────────────────────────────────────────────────────

    private async Task<List<DeploymentFreeze>> GetActiveFreezesForSpaceAsync(
        Guid spaceId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (_cache.TryGetValue(spaceId, out var cached) && (now - cached.At) < CacheTtl)
            {
                return cached.Freezes;
            }
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();
        var freezes = await db.DeploymentFreezes
            .IgnoreQueryFilters() // we filter by SpaceId explicitly below
            .Where(f => f.SpaceId == spaceId && !f.Disabled)
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        lock (_gate)
        {
            _cache[spaceId] = (freezes, time.GetUtcNow());
            return freezes;
        }
    }

    /// <summary>Drop the cache. Called on every CRUD write + by tests.</summary>
    public void InvalidateCache()
    {
        lock (_gate) { _cache.Clear(); }
    }

    // ── Validation ─────────────────────────────────────────────────────────

    private static void Validate(DeploymentFreeze input)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            throw new ArgumentException("Freeze name is required.", nameof(input));
        }
        if (input.EndUtc <= input.StartUtc)
        {
            throw new ArgumentException(
                "Freeze EndUtc must be strictly after StartUtc — a window that " +
                "ends before it starts cannot match any deployment.",
                nameof(input));
        }
    }
}
