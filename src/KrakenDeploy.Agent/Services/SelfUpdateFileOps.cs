namespace KrakenDeploy.Agent.Services;

/// <summary>
/// C6 — the roll-back-safe file operations behind the agent self-upgrade swap.
/// Pure (no logging / DI / process state) so the swap and rollback logic is
/// unit-testable without a running agent process.
/// <para>
/// The agent is NOT <c>PublishSingleFile</c>: a new apphost must load its OWN
/// managed DLLs, so the WHOLE publish directory is swapped — not just the exe
/// (swapping only the exe leaves the old DLLs in place: a version-skewed no-op or
/// a broken agent). The current install lives at the directory the supervisor
/// launches (the running process path's directory); a Windows service / systemd
/// unit relaunches that exact path, so it must ALWAYS hold a complete, runnable
/// install.
/// </para>
/// <para>
/// Locked-file rule (Windows): a loaded exe/DLL cannot be deleted or overwritten,
/// but CAN be renamed on the same volume. Every "move the current files out of
/// the way" step therefore RENAMES files to a sibling directory (backup/discard)
/// rather than deleting or overwriting them — which is why the backup and discard
/// directories MUST be on the same volume as the install directory. New files are
/// COPIED in, leaving the verified staging directory intact for a retry.
/// </para>
/// </summary>
public static class SelfUpdateFileOps
{
    /// <summary>The apphost file names produced for the agent, per OS.</summary>
    public static readonly string[] AgentExeNames =
        ["KrakenDeploy.Agent", "KrakenDeploy.Agent.exe"];

    /// <summary>
    /// Locates the agent apphost within an extracted publish directory (searches
    /// nested folders because some archives wrap the payload in a top folder).
    /// Returns null if none is present.
    /// </summary>
    public static string? FindAgentExecutable(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(dir, "KrakenDeploy.Agent*", SearchOption.AllDirectories)
            .FirstOrDefault(f =>
                AgentExeNames.Contains(Path.GetFileName(f), StringComparer.Ordinal));
    }

    /// <summary>
    /// Forward swap: back up the current install, then install the new files.
    /// After a successful return <paramref name="installDir"/> holds the NEW files
    /// and <paramref name="backupDir"/> holds the PREVIOUS files (kept until the
    /// health gate confirms the new version). On ANY failure the partial state is
    /// rolled back so <paramref name="installDir"/> holds the previous files again
    /// and the exception is rethrown — the caller keeps running the old binary and
    /// must NOT exit. <paramref name="newDir"/> is left intact (copied, not moved).
    /// <paramref name="backupDir"/> MUST be on the same volume as
    /// <paramref name="installDir"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The new payload has no agent executable, or the installed payload failed
    /// verification after the copy.
    /// </exception>
    public static void ApplySwap(string installDir, string newDir, string backupDir)
        => ApplySwap(installDir, newDir, backupDir, faultAfterBackup: null);

    /// <summary>
    /// Test seam for <see cref="ApplySwap(string,string,string)"/>:
    /// <paramref name="faultAfterBackup"/> is invoked after the current install has
    /// been moved to the backup and before the new files are copied in, to
    /// deterministically simulate the audited "copy failure mid-swap" (File.Move
    /// succeeded, File.Copy about to fail) and assert the install is restored.
    /// </summary>
    internal static void ApplySwap(
        string installDir, string newDir, string backupDir, Action? faultAfterBackup)
    {
        ArgumentException.ThrowIfNullOrEmpty(installDir);
        ArgumentException.ThrowIfNullOrEmpty(newDir);
        ArgumentException.ThrowIfNullOrEmpty(backupDir);

        if (FindAgentExecutable(newDir) is null)
        {
            throw new InvalidOperationException(
                $"Update payload at '{newDir}' contains no agent executable — refusing to swap.");
        }

        // Recover a stranded backup FIRST: if a prior swap left this install
        // incomplete (no runnable apphost) while the backup still holds one, merge
        // the backup back before doing anything else — otherwise the "start from an
        // empty backup" step below would destroy the only good copy.
        if (FindAgentExecutable(installDir) is null && FindAgentExecutable(backupDir) is not null)
        {
            MoveDirectoryContents(backupDir, installDir);
        }

        // Start from an empty backup directory.
        if (Directory.Exists(backupDir))
        {
            Directory.Delete(backupDir, recursive: true);
        }
        Directory.CreateDirectory(backupDir);

        // Tracks whether we have passed the backup move and (may) have started
        // writing NEW files into installDir — which changes how a failure is undone.
        var newFilesInInstall = false;
        try
        {
            // 1. Move the current install out to the backup (rename; keeps locked
            //    exe/DLLs intact on the same volume). If this throws PARTWAY, some
            //    originals are already in backup and the rest are still in installDir.
            MoveDirectoryContents(installDir, backupDir);
            newFilesInInstall = true;

            faultAfterBackup?.Invoke();

            // 2. Copy the new payload in (leaves staging intact for a retry).
            CopyDirectoryContents(newDir, installDir);

            // 3. Verify the installed payload is actually runnable.
            if (FindAgentExecutable(installDir) is null)
            {
                throw new InvalidOperationException(
                    $"Installed update at '{installDir}' has no agent executable after copy.");
            }
        }
        catch
        {
            // Restore the previous install so the launch path is complete again,
            // robust to a failure in EITHER phase.
            RestoreAfterFailedApply(installDir, backupDir, newFilesInInstall);
            throw;
        }
    }

    /// <summary>
    /// Probation rollback (runs inside the freshly-started NEW binary that failed
    /// its health gate): move the current install aside to
    /// <paramref name="discardDir"/>, then move the backup back into place. After
    /// return <paramref name="installDir"/> holds the PREVIOUS (known-good) files;
    /// the caller must exit so the supervisor relaunches them.
    /// <paramref name="discardDir"/> MUST be on the same volume as
    /// <paramref name="installDir"/> (the current files are loaded/locked and can
    /// only be renamed, not copied+deleted, across a volume).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The backup is missing or does not contain a runnable agent.
    /// </exception>
    public static void RestoreFromBackup(string installDir, string backupDir, string discardDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(installDir);
        ArgumentException.ThrowIfNullOrEmpty(backupDir);
        ArgumentException.ThrowIfNullOrEmpty(discardDir);

        if (FindAgentExecutable(backupDir) is null)
        {
            throw new InvalidOperationException(
                $"Cannot roll back: backup at '{backupDir}' is missing or has no agent executable.");
        }

        if (Directory.Exists(discardDir))
        {
            Directory.Delete(discardDir, recursive: true);
        }
        Directory.CreateDirectory(discardDir);

        // Move the current (unhealthy NEW) files out, then the backup back in.
        MoveDirectoryContents(installDir, discardDir);
        MoveDirectoryContents(backupDir, installDir);
    }

    /// <summary>
    /// Recursively MOVES every file from <paramref name="source"/> into
    /// <paramref name="dest"/> (creating subdirectories, overwriting existing
    /// files), then removes the now-empty source subtree — but keeps the
    /// <paramref name="source"/> root directory itself (it may be a process's
    /// launch/working directory that cannot be deleted while in use). File-granular
    /// so a locked exe/DLL in a nested folder is renamed rather than tripping a
    /// whole-directory move on an open handle. Source and dest MUST be on the same
    /// volume.
    /// </summary>
    public static void MoveDirectoryContents(string source, string dest)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(dest);

        // Materialise first — moving files out from under a lazy enumerator is
        // undefined.
        var dirs = Directory.GetDirectories(source, "*", SearchOption.AllDirectories);
        var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);

        foreach (var dir in dirs)
        {
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));
        }

        foreach (var file in files)
        {
            var target = Path.Combine(dest, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(file, target, overwrite: true);
        }

        // Drop the emptied subdirectories but keep the source root.
        foreach (var dir in Directory.GetDirectories(source))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Recursively COPIES every file from <paramref name="source"/> into
    /// <paramref name="dest"/> (creating subdirectories, overwriting existing
    /// files). Leaves the source intact.
    /// </summary>
    public static void CopyDirectoryContents(string source, string dest)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"Copy source '{source}' does not exist.");
        }

        Directory.CreateDirectory(dest);

        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dest, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    // ── internals ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Restores the previous install after a failed <see cref="ApplySwap"/>, robust
    /// to a failure in EITHER phase:
    /// <list type="bullet">
    /// <item><paramref name="newFilesInInstall"/> == false — the backup move failed
    /// partway: installDir still holds the un-moved ORIGINAL files and backup holds
    /// the rest. Merge the backup back in WITHOUT clearing (clearing would delete the
    /// un-backed-up originals).</item>
    /// <item><paramref name="newFilesInInstall"/> == true — the backup move completed
    /// and the copy-in (may have) written NEW files over the emptied install. Clear
    /// those partial copies, then restore the complete originals from backup.</item>
    /// </list>
    /// Either way installDir ends holding the complete previous version and backup is
    /// consumed. Best-effort: an interrupted restore is backed up by the stranded-backup
    /// recovery at the next <see cref="ApplySwap"/>.
    /// </summary>
    internal static void RestoreAfterFailedApply(string installDir, string backupDir, bool newFilesInInstall)
    {
        try
        {
            if (newFilesInInstall)
            {
                // Partial NEW copies are not the originals — remove them first
                // (they are freshly-copied, not loaded, so deletable).
                ClearDirectoryContents(installDir);
            }

            // Move every backed-up original back, merging with any originals still in
            // installDir from a partial backup move. Backup and install never hold the
            // same original twice, so there is no collision.
            MoveDirectoryContents(backupDir, installDir);
        }
        catch
        {
            // Best-effort: the stranded-backup recovery at the next ApplySwap (and an
            // operator restore) are the backstops for a restore that itself fails.
        }
    }

    private static void ClearDirectoryContents(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(dir))
        {
            File.Delete(file);
        }

        foreach (var sub in Directory.GetDirectories(dir))
        {
            Directory.Delete(sub, recursive: true);
        }
    }
}
