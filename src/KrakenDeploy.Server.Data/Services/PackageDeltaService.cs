using Microsoft.Extensions.Logging;
using Octodiff.Core;
using Octodiff.Diagnostics;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Server-side Octodiff helper.  Builds binary signatures and deltas for package
/// zip files so that the gRPC delivery service can send a small delta instead of
/// the full file when the agent already has a base version in its local cache.
/// <para>
/// Signatures are cached on disk alongside the package zip
/// (<c>{storedPath}.octosig</c>) so they are computed only once per version and
/// reused across subsequent deployments.
/// </para>
/// </summary>
public sealed class PackageDeltaService(ILogger<PackageDeltaService> logger)
{
    private const string SigExtension = ".octosig";

    /// <summary>
    /// Returns a <see cref="MemoryStream"/> (rewound to position 0) that contains
    /// the Octodiff delta bytes required to transform
    /// <paramref name="basePackagePath"/> into <paramref name="newPackagePath"/>.
    /// <para>
    /// Falls back to returning <c>null</c> if either file is missing, so the caller
    /// can transparently serve the full package instead.
    /// </para>
    /// </summary>
    public async Task<MemoryStream?> BuildDeltaAsync(
        string basePackagePath,
        string newPackagePath,
        CancellationToken ct)
    {
        if (!File.Exists(basePackagePath) || !File.Exists(newPackagePath))
        {
            logger.LogWarning(
                "Delta build skipped: one or both package files are missing " +
                "(base={Base}, new={New}).",
                basePackagePath, newPackagePath);
            return null;
        }

        // ── 1. Ensure the base package's signature is on disk ─────────────────
        var sigPath = basePackagePath + SigExtension;
        if (!File.Exists(sigPath))
        {
            logger.LogInformation(
                "Building Octodiff signature for {File}…",
                Path.GetFileName(basePackagePath));

            await BuildSignatureAsync(basePackagePath, sigPath, ct).ConfigureAwait(false);
        }

        // ── 2. Build the delta in memory ──────────────────────────────────────
        var baseBytes = new FileInfo(basePackagePath).Length;
        var newBytes  = new FileInfo(newPackagePath).Length;

        logger.LogInformation(
            "Building Octodiff delta: {Base} ({BaseKiB:N0} KiB) → {New} ({NewKiB:N0} KiB).",
            Path.GetFileName(basePackagePath), baseBytes / 1024,
            Path.GetFileName(newPackagePath),  newBytes  / 1024);

        var deltaBuffer = new MemoryStream();
        await BuildDeltaCoreAsync(sigPath, newPackagePath, deltaBuffer, ct).ConfigureAwait(false);
        deltaBuffer.Position = 0;

        var savings = newBytes > 0
            ? 1.0 - (double)deltaBuffer.Length / newBytes
            : 0.0;

        logger.LogInformation(
            "Delta built: {DeltaKiB:N0} KiB  (full {FullKiB:N0} KiB, saving {Saving:P0}).",
            deltaBuffer.Length / 1024, newBytes / 1024, savings);

        return deltaBuffer;
    }

    // ── Private synchronous Octodiff wrappers run on the thread pool ──────────

    private static Task BuildSignatureAsync(
        string packagePath, string sigPath, CancellationToken ct)
        => Task.Run(() =>
        {
            using var src = new FileStream(
                packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var sig = new FileStream(
                sigPath, FileMode.Create, FileAccess.Write, FileShare.None);

            new SignatureBuilder().Build(src, new SignatureWriter(sig));
        }, ct);

    private static Task BuildDeltaCoreAsync(
        string sigPath,
        string newPackagePath,
        Stream output,
        CancellationToken ct)
        => Task.Run(() =>
        {
            using var sig    = new FileStream(sigPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var newPkg = new FileStream(newPackagePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            new DeltaBuilder().BuildDelta(
                newPkg,
                new SignatureReader(sig, new NullProgressReporter()),
                new AggregateCopyOperationsDecorator(new BinaryDeltaWriter(output)));
        }, ct);
}
