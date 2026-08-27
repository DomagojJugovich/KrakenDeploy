using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Platform.Releases;

/// <summary>
/// Deploy-orchestration writes to the blue-green release registry
/// (docs/blue-green-slot-deployment.md §5): register a release as Deploying,
/// flip the current default (previous default → Draining), retire a fully
/// drained release. All transitions for one call happen in a single
/// <c>SaveChanges</c> so the registry can never be observed half-flipped.
/// <para>
/// Reads on the hot path (the per-node router) deliberately do NOT go through
/// this service — the router queries the two tables with raw Npgsql and its own
/// cache so it carries none of this project's dependency graph.
/// </para>
/// </summary>
public sealed class ReleaseRegistry(
    IDbContextFactory<PlatformReleaseDbContext> platformFactory,
    TimeProvider timeProvider,
    ILogger<ReleaseRegistry> logger)
{
    /// <summary>Sanity bound only — slot count is a tuning parameter (D-bg-4), three is the floor.</summary>
    private const short MaxSlotNo = 16;

    /// <summary>
    /// Postgres advisory-lock key serializing ALL registry transitions. The
    /// transitions are check-then-act over two tables from separate processes
    /// (CLI invocations, the drain-watcher on any instance); without a global
    /// lock, two interleaved flips can strand a second Active release, and a
    /// flip racing a retire can point the default at a Retired release (fleet-
    /// wide 503). <c>pg_advisory_xact_lock</c> auto-releases with the
    /// transaction — no leak on crash. Arbitrary but stable constant.
    /// </summary>
    private const long RegistryLockKey = 0x4B_44_52_45_4C_52_45_47; // "KDRELREG"

    /// <summary>
    /// Opens a transaction on <paramref name="db"/> and takes the global
    /// registry advisory lock, so every read inside the transition sees a state
    /// no concurrent transition can invalidate before commit.
    /// </summary>
    private static async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction>
        BeginSerializedTransitionAsync(PlatformReleaseDbContext db, CancellationToken ct)
    {
        var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await db.Database
            .ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({RegistryLockKey})", ct)
            .ConfigureAwait(false);
        return tx;
    }

    /// <summary>Registers a new release as Deploying into a free (Retired/empty) slot.</summary>
    /// <exception cref="InvalidOperationException">Duplicate id, or the slot holds a non-Retired release.</exception>
    public async Task<AppRelease> RegisterAsync(
        string releaseId, string label, short slotNo, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (slotNo is < 1 or > MaxSlotNo)
        {
            throw new InvalidOperationException(
                $"Slot must be between 1 and {MaxSlotNo} (got {slotNo}).");
        }

        await using var db = await platformFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await BeginSerializedTransitionAsync(db, ct).ConfigureAwait(false);

        if (await db.AppReleases.AnyAsync(r => r.Id == releaseId, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Release '{releaseId}' is already registered — release ids are immutable history; pick a new id.");
        }

        var occupant = await db.AppReleases
            .Where(r => r.SlotNo == slotNo && r.Status != AppReleaseStatus.Retired)
            .Select(r => new { r.Id, r.Status })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (occupant is not null)
        {
            throw new InvalidOperationException(
                $"Slot {slotNo} is occupied by release '{occupant.Id}' ({occupant.Status}). " +
                "Target the Retired slot (runbook step 0), or retire the occupant first.");
        }

        var release = new AppRelease
        {
            Id = releaseId,
            Label = label,
            SlotNo = slotNo,
            Status = AppReleaseStatus.Deploying,
            DeployedAtUtc = timeProvider.GetUtcNow(),
        };
        db.AppReleases.Add(release);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);

        logger.LogInformation(
            "Registered release {ReleaseId} ({Label}) into slot {Slot} as Deploying.",
            releaseId, label, slotNo);
        return release;
    }

    /// <summary>
    /// Makes <paramref name="releaseId"/> the current default (Active) and marks the
    /// previous default Draining with a drain deadline of now + <paramref name="drainWindow"/>.
    /// Idempotent when the release is already the Active default.
    /// </summary>
    /// <exception cref="InvalidOperationException">Unknown release, or it is Draining/Retired.</exception>
    public async Task FlipDefaultAsync(
        string releaseId, TimeSpan drainWindow, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseId);

        await using var db = await platformFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await BeginSerializedTransitionAsync(db, ct).ConfigureAwait(false);

        var release = await db.AppReleases
            .FirstOrDefaultAsync(r => r.Id == releaseId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Release '{releaseId}' is not registered.");

        if (release.Status is AppReleaseStatus.Draining or AppReleaseStatus.Retired)
        {
            throw new InvalidOperationException(
                $"Release '{releaseId}' is {release.Status} — a drained release cannot become " +
                "the default again. Register the build as a NEW release id instead.");
        }

        var setting = await db.PlatformSettings
            .FirstOrDefaultAsync(s => s.Key == PlatformSettingKeys.CurrentDefaultRelease, ct)
            .ConfigureAwait(false);
        var previousDefaultId = setting?.Value;

        if (previousDefaultId == releaseId && release.Status == AppReleaseStatus.Active)
        {
            logger.LogInformation("Release {ReleaseId} is already the Active default; no-op.", releaseId);
            return;
        }

        var now = timeProvider.GetUtcNow();

        if (previousDefaultId is not null && previousDefaultId != releaseId)
        {
            var previous = await db.AppReleases
                .FirstOrDefaultAsync(r => r.Id == previousDefaultId, ct)
                .ConfigureAwait(false);
            if (previous is not null && previous.Status == AppReleaseStatus.Active)
            {
                previous.Status = AppReleaseStatus.Draining;
                previous.DrainDeadlineUtc = now + drainWindow;
            }
        }

        release.Status = AppReleaseStatus.Active;

        if (setting is null)
        {
            db.PlatformSettings.Add(new PlatformSetting
            {
                Key = PlatformSettingKeys.CurrentDefaultRelease,
                Value = releaseId,
                ModifiedUtc = now,
            });
        }
        else
        {
            setting.Value = releaseId;
            setting.ModifiedUtc = now;
        }

        // One commit: default pointer, promotion, and demotion become visible
        // together, serialized against every other transition by the advisory lock.
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);

        logger.LogInformation(
            "Flipped current default release to {ReleaseId} (previous: {Previous}).",
            releaseId, previousDefaultId ?? "<none>");
    }

    /// <summary>
    /// Marks a fully drained (or never-flipped Deploying) release Retired, freeing
    /// its slot. Refuses to retire the current default / an Active release.
    /// </summary>
    /// <exception cref="InvalidOperationException">Unknown release, or it is the Active default.</exception>
    public async Task RetireAsync(string releaseId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseId);

        await using var db = await platformFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await BeginSerializedTransitionAsync(db, ct).ConfigureAwait(false);

        var release = await db.AppReleases
            .FirstOrDefaultAsync(r => r.Id == releaseId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Release '{releaseId}' is not registered.");

        if (release.Status == AppReleaseStatus.Retired)
        {
            logger.LogInformation("Release {ReleaseId} is already Retired; no-op.", releaseId);
            return;
        }

        var defaultId = await GetDefaultReleaseIdAsync(db, ct).ConfigureAwait(false);
        if (release.Status == AppReleaseStatus.Active || defaultId == releaseId)
        {
            throw new InvalidOperationException(
                $"Release '{releaseId}' is the Active default — flip the default to another " +
                "release first, then retire this one once drained.");
        }

        // Draining → Retired is the normal path; Deploying → Retired frees the slot
        // after a failed health-gate (the release never took traffic).
        release.Status = AppReleaseStatus.Retired;
        release.DrainedAtUtc = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);

        logger.LogInformation(
            "Retired release {ReleaseId}; slot {Slot} is free for the next deploy.",
            releaseId, release.SlotNo);
    }

    /// <summary>Current default pointer + all registered releases, newest first.</summary>
    public async Task<ReleaseRegistrySnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        await using var db = await platformFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var defaultId = await GetDefaultReleaseIdAsync(db, ct).ConfigureAwait(false);
        var releases = await db.AppReleases
            .AsNoTracking()
            .OrderByDescending(r => r.DeployedAtUtc)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new ReleaseRegistrySnapshot(defaultId, releases);
    }

    /// <summary>
    /// The releases that are LIVE from an operator command's perspective: every
    /// non-Retired row, excluding the caller's own release ONLY while that
    /// release is still <see cref="AppReleaseStatus.Deploying"/> (registered but
    /// not yet serving — the release the command is preparing). An own release
    /// that is Active or Draining IS live: running the command via
    /// <c>docker compose exec</c> into the serving slot must not exempt the very
    /// release that is serving. Pass <paramref name="ownReleaseId"/> null for no
    /// exemption at all. Used by the non-additive migration guard (BG1/T4) and
    /// the encryption rotation gate; static so CLI callers with a hand-built
    /// context share the one query shape.
    /// </summary>
    public static Task<List<AppRelease>> GetLiveReleasesExceptOwnDeployingAsync(
        PlatformReleaseDbContext db, string? ownReleaseId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.AppReleases
            .AsNoTracking()
            .Where(r => r.Status != AppReleaseStatus.Retired
                && !(r.Id == ownReleaseId && r.Status == AppReleaseStatus.Deploying))
            .OrderBy(r => r.SlotNo)
            .ToListAsync(ct);
    }

    private static async Task<string?> GetDefaultReleaseIdAsync(
        PlatformReleaseDbContext db, CancellationToken ct)
    {
        return await db.PlatformSettings
            .AsNoTracking()
            .Where(s => s.Key == PlatformSettingKeys.CurrentDefaultRelease)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }
}

/// <summary>Point-in-time view of the release registry.</summary>
public sealed record ReleaseRegistrySnapshot(
    string? DefaultReleaseId,
    IReadOnlyList<AppRelease> Releases);
