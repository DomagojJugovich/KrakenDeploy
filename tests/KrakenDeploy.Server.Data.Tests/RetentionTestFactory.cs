using KrakenDeploy.Server.Core.Domain.Packages;
using KrakenDeploy.Server.Data.Accounts;
using KrakenDeploy.Server.Data.ArtifactStorage;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Data.Spaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Shared construction helper for <see cref="RetentionService"/> in tests. WP9 grew
/// the constructor (artifact + package stores, account context, settings, config);
/// this centralises the no-op wiring so each test only supplies what it cares about
/// (e.g. a real <see cref="IArtifactStore"/> over a temp dir for the file-cleanup
/// tests). Defaults to single-instance (<see cref="DisabledAccountContext"/>) with a
/// throwaway <c>Server:DataPath</c>.
/// </summary>
internal static class RetentionTestFactory
{
    public static RetentionService NewService(
        PostgresFixture postgres,
        IArtifactStore? artifactStore = null,
        IPackageStore? packageStore = null,
        string? dataPath = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Server:DataPath"] = dataPath
                    ?? Path.Combine(Path.GetTempPath(), $"kraken-ret-{Guid.NewGuid():N}"),
            })
            .Build();

        return new RetentionService(
            postgres,
            new DefaultSpaceContext(),
            artifactStore ?? new NoopArtifactStore(),
            packageStore ?? new NoopPackageStore(),
            new DisabledAccountContext(),
            new SettingsService(postgres.ScopeFactory, TimeProvider.System),
            config,
            NullLogger<RetentionService>.Instance);
    }

    /// <summary>No-op artifact store — deletes nothing, records nothing.</summary>
    private sealed class NoopArtifactStore : IArtifactStore
    {
        public Task<string> SaveAsync(
            Guid deploymentId, string stepName, string fileName, Stream content,
            CancellationToken ct = default)
            => Task.FromResult($"{deploymentId:N}/{stepName}/{fileName}");

        public Task<Stream> OpenReadAsync(string storedPath, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream());

        public void Delete(string storedPath) { }
    }

    /// <summary>No-op package store — deletes nothing.</summary>
    private sealed class NoopPackageStore : IPackageStore
    {
        public Task<string> StoreAsync(
            string packageId, string version, string fileName, Stream content,
            CancellationToken ct)
            => Task.FromResult($"{packageId}/{version}/{fileName}");

        public Task<Stream> OpenReadAsync(string storedPath, CancellationToken ct)
            => Task.FromResult<Stream>(new MemoryStream());

        public string GetFullPath(string storedPath) => storedPath;

        public Task DeleteAsync(string storedPath, CancellationToken ct) => Task.CompletedTask;
    }
}
