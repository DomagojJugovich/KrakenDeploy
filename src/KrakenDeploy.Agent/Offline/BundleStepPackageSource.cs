using KrakenDeploy.Agent.Transport;
using KrakenDeploy.Contracts.Offline;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Agent.Offline;

/// <summary>
/// Offline <see cref="IStepPackageSource"/>: extracts a step-handler package
/// from the bundle's <c>step-packages/{name}/{version}/</c> directory into the
/// loader's cache, instead of streaming it from the server. The
/// <paramref name="extract"/> callback is wired to
/// <c>StepPackageLoader.ExtractToCache</c> (same contract as the gRPC source).
/// </summary>
public sealed class BundleStepPackageSource(
    string bundleRoot,
    Func<string, string, string, Task> extract,
    ILogger<BundleStepPackageSource> logger) : IStepPackageSource
{
    public async Task EnsureExtractedAsync(string name, string version, CancellationToken ct)
    {
        var dir = Path.Combine(
            bundleRoot, OfflineBundleLayout.StepPackagesDir, name, version);
        if (!Directory.Exists(dir))
        {
            throw new FileNotFoundException(
                $"Offline bundle is missing step package {name} v{version} (expected at '{dir}').");
        }

        var archive = Directory.EnumerateFiles(dir).FirstOrDefault()
            ?? throw new FileNotFoundException(
                $"Offline bundle directory for step package {name} v{version} contains no archive.");

        await extract(name, version, archive).ConfigureAwait(false);
        logger.LogInformation(
            "Extracted step package {Name} {Version} from offline bundle.", name, version);
    }
}
