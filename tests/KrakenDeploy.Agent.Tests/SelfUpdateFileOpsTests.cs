using FluentAssertions;
using KrakenDeploy.Agent.Services;

namespace KrakenDeploy.Agent.Tests;

/// <summary>
/// C6 — unit tests for the roll-back-safe self-upgrade file operations. Every
/// test works on temporary directories that are cleaned up afterwards; no running
/// agent process is involved (the operations are pure file I/O).
/// </summary>
public sealed class SelfUpdateFileOpsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"kraken-selfupdate-{Guid.NewGuid():N}");

    private string InstallDir => Path.Combine(_root, "install");
    private string NewDir     => Path.Combine(_root, "new");
    private string BackupDir  => Path.Combine(_root, "install.backup");
    private string DiscardDir => Path.Combine(_root, "install.failed");

    public SelfUpdateFileOpsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* non-fatal */ }
    }

    // A realistic multi-file publish payload: apphost, config, a managed DLL, a
    // nested satellite resources DLL, and a nested native runtime asset.
    private static Dictionary<string, string> Payload(string tag) => new()
    {
        ["KrakenDeploy.Agent.exe"]                 = $"{tag}-apphost",
        ["KrakenDeploy.Agent.dll"]                 = $"{tag}-managed",
        ["appsettings.json"]                       = $"{{\"tag\":\"{tag}\"}}",
        ["hr/KrakenDeploy.Agent.resources.dll"]    = $"{tag}-satellite",
        ["runtimes/win-x64/native/extra.dll"]      = $"{tag}-native",
    };

    private static void Materialise(string root, Dictionary<string, string> files)
    {
        foreach (var (rel, content) in files)
        {
            var path = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
    }

    private static Dictionary<string, string> Snapshot(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(root))
        {
            return result;
        }

        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            result[rel] = File.ReadAllText(file);
        }

        return result;
    }

    // ── ApplySwap ────────────────────────────────────────────────────────────

    [Fact]
    public void ApplySwap_installs_full_multifile_payload_and_backs_up_previous()
    {
        var previous = Payload("old");
        var incoming = Payload("new");
        Materialise(InstallDir, previous);
        Materialise(NewDir, incoming);

        SelfUpdateFileOps.ApplySwap(InstallDir, NewDir, BackupDir);

        // The WHOLE payload — including the nested satellite + native assets — is
        // installed, not just the exe (acceptance: multi-file payload fully installed).
        Snapshot(InstallDir).Should().BeEquivalentTo(incoming);
        // The previous version is preserved in the backup for the health gate.
        Snapshot(BackupDir).Should().BeEquivalentTo(previous);
        // The staging payload is left intact (copied, not moved).
        Snapshot(NewDir).Should().BeEquivalentTo(incoming);
    }

    [Fact]
    public void ApplySwap_rolls_back_to_previous_when_copy_fails_midswap()
    {
        var previous = Payload("old");
        Materialise(InstallDir, previous);
        Materialise(NewDir, Payload("new"));

        // Simulate the audited failure: the backup move succeeded, the copy is
        // about to fail (disk full / AV lock).
        var act = () => SelfUpdateFileOps.ApplySwap(
            InstallDir, NewDir, BackupDir,
            faultAfterBackup: () => throw new IOException("simulated disk-full mid-swap"));

        act.Should().Throw<IOException>();

        // The install directory is fully restored to the PREVIOUS version — the
        // agent still boots the old binary (acceptance criterion 1).
        Snapshot(InstallDir).Should().BeEquivalentTo(previous);
        SelfUpdateFileOps.FindAgentExecutable(InstallDir).Should().NotBeNull();
    }

    [Fact]
    public void RestoreAfterFailedApply_merges_originals_back_after_a_partial_backup_move()
    {
        // Simulate a backup move that threw partway: some originals already in backup,
        // the rest still in installDir, no new files copied yet.
        Materialise(InstallDir, new Dictionary<string, string> { ["B.dll"] = "old-B" });
        Materialise(BackupDir, new Dictionary<string, string>
        {
            ["KrakenDeploy.Agent.exe"] = "old-apphost",
            ["A.dll"]                  = "old-A",
        });

        SelfUpdateFileOps.RestoreAfterFailedApply(InstallDir, BackupDir, newFilesInInstall: false);

        // The complete original is reassembled from both places — the un-backed-up
        // remnant (B) is NOT lost.
        Snapshot(InstallDir).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["KrakenDeploy.Agent.exe"] = "old-apphost",
            ["A.dll"]                  = "old-A",
            ["B.dll"]                  = "old-B",
        });
        SelfUpdateFileOps.FindAgentExecutable(InstallDir).Should().NotBeNull();
    }

    [Fact]
    public void RestoreAfterFailedApply_clears_partial_new_copies_after_a_copy_phase_fault()
    {
        // Simulate a copy-in that threw partway: install holds partial NEW copies,
        // backup holds the complete originals.
        Materialise(InstallDir, new Dictionary<string, string>
        {
            ["KrakenDeploy.Agent.exe"] = "new-apphost",
            ["C.dll"]                  = "new-only-file",
        });
        Materialise(BackupDir, Payload("old"));

        SelfUpdateFileOps.RestoreAfterFailedApply(InstallDir, BackupDir, newFilesInInstall: true);

        // Partial new copies (including the new-only file) are gone; originals restored.
        Snapshot(InstallDir).Should().BeEquivalentTo(Payload("old"));
    }

    [Fact]
    public void ApplySwap_recovers_a_stranded_backup_before_swapping()
    {
        // A prior swap left the install broken (no apphost) with the originals
        // stranded in the backup.
        Directory.CreateDirectory(InstallDir); // gutted install (no apphost)
        var stranded = Payload("old");
        Materialise(BackupDir, stranded);
        var incoming = Payload("new");
        Materialise(NewDir, incoming);

        SelfUpdateFileOps.ApplySwap(InstallDir, NewDir, BackupDir);

        // The swap completes AND the stranded backup was not destroyed — it became
        // the new backup of the recovered previous version.
        Snapshot(InstallDir).Should().BeEquivalentTo(incoming);
        Snapshot(BackupDir).Should().BeEquivalentTo(stranded);
    }

    [Fact]
    public void ApplySwap_refuses_and_leaves_install_untouched_when_payload_has_no_exe()
    {
        var previous = Payload("old");
        Materialise(InstallDir, previous);
        Materialise(NewDir, new Dictionary<string, string> { ["readme.txt"] = "no exe here" });

        var act = () => SelfUpdateFileOps.ApplySwap(InstallDir, NewDir, BackupDir);

        act.Should().Throw<InvalidOperationException>();
        // Nothing was disturbed — the check happens before any move.
        Snapshot(InstallDir).Should().BeEquivalentTo(previous);
        Directory.Exists(BackupDir).Should().BeFalse();
    }

    // ── RestoreFromBackup (probation rollback) ────────────────────────────────

    [Fact]
    public void RestoreFromBackup_replaces_new_install_with_backup()
    {
        var current = Payload("new");   // the unhealthy new version now installed
        var backup  = Payload("old");   // the known-good previous version
        Materialise(InstallDir, current);
        Materialise(BackupDir, backup);

        SelfUpdateFileOps.RestoreFromBackup(InstallDir, BackupDir, DiscardDir);

        // The install is the previous (known-good) version again.
        Snapshot(InstallDir).Should().BeEquivalentTo(backup);
        // The unhealthy files were set aside for diagnostics, not deleted.
        Snapshot(DiscardDir).Should().BeEquivalentTo(current);
    }

    [Fact]
    public void RestoreFromBackup_throws_when_backup_is_incomplete()
    {
        var current = Payload("new");
        Materialise(InstallDir, current);
        // Backup missing its apphost — not safely restorable.
        Materialise(BackupDir, new Dictionary<string, string> { ["appsettings.json"] = "{}" });

        var act = () => SelfUpdateFileOps.RestoreFromBackup(InstallDir, BackupDir, DiscardDir);

        act.Should().Throw<InvalidOperationException>();
        // The current install is left as-is (we did not strip it for a rollback we
        // cannot complete).
        Snapshot(InstallDir).Should().BeEquivalentTo(current);
    }

    // ── Primitives ────────────────────────────────────────────────────────────

    [Fact]
    public void FindAgentExecutable_locates_a_nested_apphost()
    {
        var exe = Path.Combine(NewDir, "publish", "KrakenDeploy.Agent.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "apphost");

        SelfUpdateFileOps.FindAgentExecutable(NewDir).Should().Be(exe);
    }

    [Fact]
    public void FindAgentExecutable_returns_null_when_absent()
    {
        Materialise(NewDir, new Dictionary<string, string> { ["other.dll"] = "x" });
        SelfUpdateFileOps.FindAgentExecutable(NewDir).Should().BeNull();
    }

    [Fact]
    public void CopyDirectoryContents_preserves_nested_structure()
    {
        var files = Payload("copy");
        Materialise(NewDir, files);
        var dest = Path.Combine(_root, "copy-dest");

        SelfUpdateFileOps.CopyDirectoryContents(NewDir, dest);

        Snapshot(dest).Should().BeEquivalentTo(files);
        Snapshot(NewDir).Should().BeEquivalentTo(files); // source untouched
    }

    [Fact]
    public void MoveDirectoryContents_moves_all_files_and_keeps_source_root()
    {
        var files = Payload("move");
        Materialise(InstallDir, files);
        var dest = Path.Combine(_root, "move-dest");

        SelfUpdateFileOps.MoveDirectoryContents(InstallDir, dest);

        Snapshot(dest).Should().BeEquivalentTo(files);
        Directory.Exists(InstallDir).Should().BeTrue();           // root retained
        Snapshot(InstallDir).Should().BeEmpty();                  // contents gone
    }
}
