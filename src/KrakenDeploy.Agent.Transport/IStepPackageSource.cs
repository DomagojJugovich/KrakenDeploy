namespace KrakenDeploy.Agent.Transport;

/// <summary>
/// Pluggable transport for step-package archives (Phase D-5).
/// <para>
/// The agent's <c>StepPackageLoader</c> takes an optional dependency on this
/// interface so it can pull a missing package from wherever the host wires up
/// — production wires the real <see cref="GrpcStepPackageDownloader"/>; tests
/// can stub it out without standing up a gRPC server.
/// </para>
/// <para>
/// Implementations are responsible for streaming the bytes, verifying their
/// SHA-256 against the trailer the server sent, and extracting the archive
/// into the loader's cache directory (typically via
/// <c>StepPackageLoader.ExtractToCache</c>).  On success the package is on
/// disk and the loader's next <c>TryLoad</c> call will find it.
/// </para>
/// </summary>
public interface IStepPackageSource
{
    /// <summary>
    /// Ensures the package identified by <paramref name="name"/> and
    /// <paramref name="version"/> is present in the loader's cache directory.
    /// No-op when already cached (idempotent). Throws on transport failure,
    /// hash mismatch, or when the server reports the package is not installed.
    /// </summary>
    Task EnsureExtractedAsync(string name, string version, CancellationToken ct);
}
