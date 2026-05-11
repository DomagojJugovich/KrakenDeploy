using KrakenDeploy.Server.Core.Domain.Packages;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Manages package upload, listing, retrieval, and deletion.
/// Physical storage is delegated to <see cref="IPackageStore"/>.
/// </summary>
public class PackageService(IDbContextFactory<KrakenDbContext> dbFactory, IPackageStore store, TimeProvider timeProvider)
{
    private static readonly char[] InvalidChars = ['/', '\\', ' '];

    // ── Upload ─────────────────────────────────────────────────────────────

    public async Task<Package> UploadAsync(
        string packageId,
        string version,
        string fileName,
        Stream content,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(content);

        if (packageId.IndexOfAny(InvalidChars) >= 0 || version.IndexOfAny(InvalidChars) >= 0
            || packageId.Contains("..", StringComparison.Ordinal)
            || version.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("PackageId and Version must not contain path separators.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var existing = await db.Packages
            .AnyAsync(p => p.PackageId == packageId && p.Version == version, ct)
            .ConfigureAwait(false);

        if (existing)
        {
            throw new InvalidOperationException(
                $"Package '{packageId}' version '{version}' already exists.");
        }

        // Store the file first so we can get its size.
        using var measured = new MeasuredStream(content);
        var storedPath = await store.StoreAsync(packageId, version, fileName, measured, ct)
            .ConfigureAwait(false);

        var package = new Package
        {
            PackageId = packageId,
            Version = version,
            FileName = fileName,
            StoredPath = storedPath,
            SizeBytes = measured.BytesRead,
            UploadedUtc = timeProvider.GetUtcNow(),
        };

        db.Packages.Add(package);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return package;
    }

    // ── Query ──────────────────────────────────────────────────────────────

    /// <summary>Returns distinct package IDs with the count of available versions.</summary>
    public async Task<List<PackageSummary>> GetSummariesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // EF Core / Npgsql cannot translate a `new PackageSummary(...)`
        // positional-record projection inside a GroupBy. Project to an
        // anonymous type first (translates fine) and map to the record
        // after materialization.
        var rows = await db.Packages
            .GroupBy(p => p.PackageId)
            .Select(g => new
            {
                PackageId = g.Key,
                VersionCount = g.Count(),
                LastUploaded = g.Max(p => p.UploadedUtc),
            })
            .OrderBy(s => s.PackageId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .Select(r => new PackageSummary(r.PackageId, r.VersionCount, r.LastUploaded))
            .ToList();
    }

    /// <summary>Returns all versions of a specific package, newest first.</summary>
    public async Task<List<Package>> GetVersionsAsync(string packageId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Packages
            .Where(p => p.PackageId == packageId)
            .OrderByDescending(p => p.UploadedUtc)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<Package?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Packages.FindAsync([id], ct).AsTask();
    }

    public async Task<Package?> GetAsync(
        string packageId, string version, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Packages
            .FirstOrDefaultAsync(p => p.PackageId == packageId && p.Version == version, ct)
            .ConfigureAwait(false);
    }

    // ── Download ───────────────────────────────────────────────────────────

    public async Task<(Stream stream, Package package)> OpenStreamAsync(
        Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var package = await db.Packages.FindAsync([id], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Package {id} not found.");
        var stream = await store.OpenReadAsync(package.StoredPath, ct).ConfigureAwait(false);
        return (stream, package);
    }

    // ── Delete ─────────────────────────────────────────────────────────────

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var package = await db.Packages.FindAsync([id], ct).ConfigureAwait(false);
        if (package is null)
        {
            return false;
        }

        await store.DeleteAsync(package.StoredPath, ct).ConfigureAwait(false);
        db.Packages.Remove(package);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    // ── Inner helpers ──────────────────────────────────────────────────────

    /// <summary>Wraps a stream to count the bytes read through it.</summary>
    private sealed class MeasuredStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            BytesRead += n;
            return n;
        }

        public override async Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var n = await inner.ReadAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false);
            BytesRead += n;
            return n;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = await inner.ReadAsync(buffer, ct).ConfigureAwait(false);
            BytesRead += n;
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

public sealed record PackageSummary(string PackageId, int VersionCount, DateTimeOffset LastUploaded);
