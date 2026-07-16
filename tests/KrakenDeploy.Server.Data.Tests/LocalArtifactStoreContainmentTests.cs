using FluentAssertions;
using KrakenDeploy.Server.Data.Accounts;
using KrakenDeploy.Server.Data.ArtifactStorage;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// T0-5 (WP A1) scope-3 parity: the artifact store, like the package store,
/// must never read/write/delete outside its root. <see cref="LocalArtifactStore"/>
/// already sanitises the step + file names on write; these pin the
/// defence-in-depth containment assertion added on top (the read/delete inputs
/// are server-constructed from sanitised parts today, so this is belt-and-braces,
/// but the guard must exist regardless of how the path was produced).
/// </summary>
public sealed class LocalArtifactStoreContainmentTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "kd-artstore-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }

    private LocalArtifactStore NewStore() => new(_root, new DisabledAccountContext());

    [Fact]
    public void OpenReadAsync_throws_when_the_stored_path_escapes_the_root()
    {
        var store = NewStore();
        var act = () => store.OpenReadAsync("../../../../../../etc/passwd");
        act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void Delete_throws_when_the_stored_path_escapes_the_root()
    {
        var store = NewStore();
        var act = () => store.Delete("../../../../../../etc/passwd");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task SaveAsync_neutralises_a_malicious_step_and_file_name()
    {
        var store = NewStore();
        using var content = new MemoryStream([1, 2, 3]);

        // Separators in the step/file name are collapsed by SanitiseName, and
        // the containment assertion backs that up — the file lands strictly
        // under the deployment's artifact directory, never outside the root.
        var stored = await store.SaveAsync(
            Guid.NewGuid(), "../../evil-step", "../../evil.txt", content);

        // The store reads it back (which itself re-asserts containment) — proof
        // the write landed inside the root, addressable by the returned path.
        await using (var read = await store.OpenReadAsync(stored))
        {
            using var ms = new MemoryStream();
            await read.CopyToAsync(ms);
            ms.ToArray().Should().Equal(1, 2, 3);
        }

        // Nothing escaped: the temp root's PARENT must hold no stray "evil" entry.
        var rootParent = Directory.GetParent(_root)!.FullName;
        Directory.EnumerateFileSystemEntries(rootParent, "*evil*").Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAsync_then_OpenReadAsync_round_trips_a_normal_artifact()
    {
        var store = NewStore();
        var deploymentId = Guid.NewGuid();
        using (var content = new MemoryStream([10, 20, 30]))
        {
            var stored = await store.SaveAsync(deploymentId, "Deploy Web", "output.log", content);

            await using var read = await store.OpenReadAsync(stored);
            using var ms = new MemoryStream();
            await read.CopyToAsync(ms);
            ms.ToArray().Should().Equal(10, 20, 30);
        }
    }
}
