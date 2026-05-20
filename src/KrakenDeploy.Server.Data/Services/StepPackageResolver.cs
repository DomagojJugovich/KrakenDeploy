using KrakenDeploy.Server.Core.Domain.StepPackages;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Resolves "what step-package version should I pin?" given a step type
/// (Phase D-6). The lookup queries <see cref="StepPackage"/> rows for any
/// install whose <see cref="StepPackage.StepTypes"/> denormalised list
/// contains the requested step type, then picks the highest semver.
/// <para>
/// Returns <c>null</c> when no installed package claims the step type —
/// the caller falls back to a hardcoded built-in handler. Once Phase D-8
/// has moved every built-in into a real package, the null path narrows to
/// "unknown step type" and the editor's UX will refuse the step earlier.
/// </para>
/// </summary>
public sealed class StepPackageResolver(IDbContextFactory<KrakenDbContext> dbFactory)
{
    /// <summary>
    /// Returns the (<c>name</c>, <c>version</c>) of the highest-semver
    /// installed package that claims <paramref name="stepType"/>, or
    /// <c>null</c> when none do. Case-insensitive on the step type (mirrors
    /// the denormalised lower-case storage in <see cref="StepPackage.StepTypes"/>).
    /// </summary>
    public async Task<StepPackagePin?> ResolveLatestForStepTypeAsync(
        string stepType, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepType);
        var needle = stepType.ToLowerInvariant();

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Fetch every installed (name, version) whose denormalised step-types
        // list contains the type. We post-filter in-memory because PostgreSQL's
        // ILIKE on a comma-joined string is fine for the install volumes here
        // (a server typically has tens of packages, not thousands).
        var candidates = await db.StepPackages
            .Where(p => EF.Functions.ILike("," + p.StepTypes + ",", "%," + needle + ",%"))
            .Select(p => new { p.Name, p.Version })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (candidates.Count == 0) { return null; }

        var winningVersion = PickHighestSemver(candidates.Select(c => c.Version).ToList());
        if (winningVersion is null) { return null; }

        var winning = candidates.First(c => c.Version == winningVersion);
        return new StepPackagePin(winning.Name, winning.Version);
    }

    /// <summary>
    /// Returns the highest-semver installed version of the package named
    /// <paramref name="name"/>, or <c>null</c> when no install exists.
    /// Used by the editor's version dropdown (D-7) and by the bulk-upgrade
    /// tool (D-10).
    /// </summary>
    public async Task<string?> ResolveLatestVersionByNameAsync(
        string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var versions = await db.StepPackages
            .Where(p => p.Name == name)
            .Select(p => p.Version)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return PickHighestSemver(versions);
    }

    /// <summary>
    /// Returns the latest <see cref="StepPackagePin"/> for a known package
    /// name (e.g. user re-pins after a manifest's step-type list changed).
    /// </summary>
    public async Task<StepPackagePin?> ResolveLatestForNameAsync(
        string name, CancellationToken ct = default)
    {
        var version = await ResolveLatestVersionByNameAsync(name, ct).ConfigureAwait(false);
        return version is null ? null : new StepPackagePin(name, version);
    }

    /// <summary>
    /// Picks the highest semver from a candidate list.
    /// <para>
    /// Comparison is by numeric MAJOR.MINOR.PATCH; anything after a <c>-</c>
    /// is treated as a pre-release suffix and orders BELOW the same MMP with
    /// no suffix (so <c>2.0.0-rc.1</c> &lt; <c>2.0.0</c>). Pre-release
    /// suffixes themselves order lexicographically; this matches SemVer 2.0.0
    /// closely enough for the install picker's purposes without pulling
    /// NuGet.Versioning into Server.Data just for one comparator.
    /// </para>
    /// </summary>
    internal static string? PickHighestSemver(IReadOnlyCollection<string> versions)
    {
        if (versions.Count == 0) { return null; }

        return versions
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .OrderByDescending(v => v, SemVerComparer.Instance)
            .FirstOrDefault();
    }

    /// <summary>
    /// Best-effort semver comparator (MAJOR.MINOR.PATCH + optional <c>-pre</c>).
    /// Public for the test project; not on the SDK surface.
    /// </summary>
    internal sealed class SemVerComparer : IComparer<string>
    {
        public static readonly SemVerComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) { return 0; }
            if (x is null) { return -1; }
            if (y is null) { return 1; }

            var (xCore, xPre) = Split(x);
            var (yCore, yPre) = Split(y);

            for (var i = 0; i < 3; i++)
            {
                var cmp = xCore[i].CompareTo(yCore[i]);
                if (cmp != 0) { return cmp; }
            }

            // Equal MMP — a missing pre-release suffix beats a present one.
            if (xPre is null && yPre is null) { return 0; }
            if (xPre is null) { return  1; }
            if (yPre is null) { return -1; }

            return string.Compare(xPre, yPre, StringComparison.OrdinalIgnoreCase);
        }

        private static (int[] Core, string? PreRelease) Split(string version)
        {
            var dashIndex   = version.IndexOf('-', StringComparison.Ordinal);
            var corePart    = dashIndex >= 0 ? version[..dashIndex] : version;
            var preRelease  = dashIndex >= 0 ? version[(dashIndex + 1)..] : null;
            var parts       = corePart.Split('.');
            var core        = new int[3];
            for (var i = 0; i < 3; i++)
            {
                if (i < parts.Length && int.TryParse(parts[i], out var n))
                {
                    core[i] = n;
                }
            }
            return (core, preRelease);
        }
    }
}

/// <summary>
/// A resolved (name, version) pair the agent can hand directly to its
/// <c>StepPackageLoader</c>. Returned by <see cref="StepPackageResolver"/>.
/// </summary>
public sealed record StepPackagePin(string Name, string Version);
