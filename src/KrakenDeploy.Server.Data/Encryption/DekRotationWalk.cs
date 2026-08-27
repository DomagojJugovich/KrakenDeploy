using KrakenDeploy.Contracts.Crypto;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Variables;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Encryption;

/// <summary>
/// The DEK-rotation re-encryption walk (M13.D.2): decrypts every secret under
/// the old DEK and re-encrypts it under the new one, on a tracked context, in
/// the caller's transaction. Extracted from the CLI so it is directly testable.
/// <para>
/// Two invariants the walk depends on:
/// </para>
/// <list type="number">
///   <item><b><c>IgnoreQueryFilters()</c> everywhere</b> — the caller (CLI) has no
///     active Space, so the global Space filter would silently skip
///     <c>ISpaceScoped</c> rows and leave them under the old DEK.</item>
///   <item><b>JSONB props are reassigned / flagged modified</b> — the jsonb value
///     converter has no <c>ValueComparer</c>, so in-place edits are invisible to
///     change tracking; the release-snapshot list is rebuilt+reassigned and the
///     offline-drop config is flagged <c>IsModified</c>.</item>
/// </list>
/// <para>
/// The completeness of the store list here is guarded by a reflection test that
/// fails CI if a new <c>*Encrypted</c> domain property appears un-walked.
/// </para>
/// </summary>
public static class DekRotationWalk
{
    /// <summary>
    /// Re-encrypts every secret store from <paramref name="oldDek"/> to
    /// <paramref name="newDek"/>. Does NOT save — the caller owns the
    /// transaction + <c>SaveChanges</c> + the wrapped-DEK swap.
    /// </summary>
    public static async Task<DekReEncryptCounts> ReEncryptAllAsync(
        KrakenDbContext db, byte[] oldDek, byte[] newDek, CancellationToken ct = default)
    {
        static string Re(byte[] o, byte[] n, string cipher) =>
            AesGcmCipher.Encrypt(n, AesGcmCipher.Decrypt(o, cipher));

        var c = new DekReEncryptCounts();

        // 1. Live sensitive variables (scalar column).
        var vars = await db.Variables.IgnoreQueryFilters()
            .Where(v => v.Type == VariableType.Sensitive).ToListAsync(ct).ConfigureAwait(false);
        foreach (var v in vars)
        {
            if (!string.IsNullOrEmpty(v.Value)) { v.Value = Re(oldDek, newDek, v.Value); c.Variables++; }
        }

        // 2. Release variable snapshots (JSONB — rebuild + REASSIGN the list).
        var releases = await db.Releases.IgnoreQueryFilters().ToListAsync(ct).ConfigureAwait(false);
        foreach (var release in releases)
        {
            if (release.VariableSnapshot.Count == 0) { continue; }
            var rewritten = new List<VariableSnapshot>(release.VariableSnapshot.Count);
            var touched = false;
            foreach (var s in release.VariableSnapshot)
            {
                if (s.Type == VariableType.Sensitive && !string.IsNullOrEmpty(s.Value))
                {
                    rewritten.Add(new VariableSnapshot
                    {
                        Name = s.Name, Value = Re(oldDek, newDek, s.Value),
                        Type = s.Type, Scope = s.Scope, Layer = s.Layer,
                        IsPrompted = s.IsPrompted,
                        PromptLabel = s.PromptLabel,
                        PromptDescription = s.PromptDescription,
                        PromptRequired = s.PromptRequired,
                        PromptControl = s.PromptControl,
                        PromptOptions = s.PromptOptions is null ? null : [.. s.PromptOptions],
                    });
                    touched = true;
                    c.SnapshotEntries++;
                }
                else
                {
                    rewritten.Add(s);
                }
            }
            if (touched) { release.VariableSnapshot = rewritten; c.Releases++; }
        }

        // 3. Settings documents (unified `settings` table) — re-encrypts every
        //    *Encrypted member of every ISettingsDocument generically. Covers the
        //    server-wide SMTP password AND the per-Space AI API key that used to be
        //    walked as their own steps, plus any future secret-bearing settings
        //    document, so a new one can't be silently missed by a rotation.
        c.Settings = await SettingsService.ReEncryptSettingsForRotationAsync(
            db, cipher => Re(oldDek, newDek, cipher), ct).ConfigureAwait(false);

        // 4. OIDC client secrets.
        var idps = await db.IdentityProviders.IgnoreQueryFilters()
            .Where(i => i.ClientSecretEncrypted != null).ToListAsync(ct).ConfigureAwait(false);
        foreach (var i in idps) { i.ClientSecretEncrypted = Re(oldDek, newDek, i.ClientSecretEncrypted!); c.IdentityProviders++; }

        // 5. Offline-drop config (JSONB — mutate in place + flag modified, since the
        //    converter has no ValueComparer so change tracking won't notice otherwise).
        var targets = await db.DeploymentTargets.IgnoreQueryFilters()
            .Where(t => t.OfflineDropConfig != null).ToListAsync(ct).ConfigureAwait(false);
        foreach (var t in targets)
        {
            var cfg = t.OfflineDropConfig!;
            var any = false;
            if (!string.IsNullOrEmpty(cfg.HmacKeyEncrypted)) { cfg.HmacKeyEncrypted = Re(oldDek, newDek, cfg.HmacKeyEncrypted); any = true; }
            if (!string.IsNullOrEmpty(cfg.BundleKeyEncrypted)) { cfg.BundleKeyEncrypted = Re(oldDek, newDek, cfg.BundleKeyEncrypted); any = true; }
            if (!string.IsNullOrEmpty(cfg.SmtpPasswordEncrypted)) { cfg.SmtpPasswordEncrypted = Re(oldDek, newDek, cfg.SmtpPasswordEncrypted); any = true; }
            if (!string.IsNullOrEmpty(cfg.WebhookSecretEncrypted)) { cfg.WebhookSecretEncrypted = Re(oldDek, newDek, cfg.WebhookSecretEncrypted); any = true; }
            if (!string.IsNullOrEmpty(cfg.FileSharePasswordEncrypted)) { cfg.FileSharePasswordEncrypted = Re(oldDek, newDek, cfg.FileSharePasswordEncrypted); any = true; }
            if (any)
            {
                db.Entry(t).Property(x => x.OfflineDropConfig).IsModified = true;
                c.OfflineDropFields++;
            }
        }

        // 6. Sensitive task output variables (scalar column; only IsSensitive rows
        //    hold ciphertext — plaintext outputs are left untouched). This column
        //    is dual-mode (plaintext OR ciphertext), so it is not named *Encrypted
        //    and the reflection completeness test can't see it — it is walked here
        //    explicitly and asserted by DekRotationWalkTests instead.
        var outputs = await db.TaskOutputVariables.IgnoreQueryFilters()
            .Where(o => o.IsSensitive).ToListAsync(ct).ConfigureAwait(false);
        foreach (var o in outputs)
        {
            if (!string.IsNullOrEmpty(o.Value)) { o.Value = Re(oldDek, newDek, o.Value); c.OutputVariables++; }
        }

        // 7. WP3 — paused tasks' resume checkpoints (scalar column). Non-null only
        //    while a task sits Paused at a manual-intervention gate, but the payload
        //    embeds captured SENSITIVE output values, so a rotation that skipped it
        //    would make every in-flight approval un-resumable (the decrypt under the
        //    new DEK would throw and the task would fail on approve).
        //    WP3-b — excludes the empty string too, matching every sibling step above.
        //    An empty checkpoint from any future writer would otherwise reach
        //    AesGcmCipher.Decrypt("") and abort the entire rotation.
        var paused = await db.ServerTasks.IgnoreQueryFilters()
            .Where(t => t.PauseCheckpointEncrypted != null && t.PauseCheckpointEncrypted != "")
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var t in paused)
        {
            t.PauseCheckpointEncrypted = Re(oldDek, newDek, t.PauseCheckpointEncrypted!);
            c.PauseCheckpoints++;
        }

        // 8. Prompted-variable form payloads. Only the nested
        // SensitiveValuesEncrypted member is rewritten; non-sensitive values stay as-is.
        var promptedTasks = await db.ServerTasks.IgnoreQueryFilters()
            .Where(t => t.FormValues != null)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var task in promptedTasks)
        {
            var rewritten = PromptedVariableFormValuesCodec.ReEncrypt(
                task.FormValues!, cipher => Re(oldDek, newDek, cipher));
            if (!string.Equals(rewritten, task.FormValues, StringComparison.Ordinal))
            {
                task.FormValues = rewritten;
                c.PromptedVariablePayloads++;
            }
        }

        return c;
    }
}

/// <summary>Per-store re-encryption counts for the operator summary + audit.</summary>
public sealed class DekReEncryptCounts
{
    public int Variables { get; set; }
    public int Releases { get; set; }
    public int SnapshotEntries { get; set; }

    /// <summary>
    /// <c>*Encrypted</c> members rewritten across all settings documents (SMTP
    /// password, AI API key, and any future secret-bearing settings document).
    /// </summary>
    public int Settings { get; set; }
    public int IdentityProviders { get; set; }
    public int OfflineDropFields { get; set; }

    /// <summary>Sensitive task output variables (T0-6) re-encrypted.</summary>
    public int OutputVariables { get; set; }

    /// <summary>WP3 — resume checkpoints of tasks paused at a manual-intervention
    /// gate. Normally 0; non-zero only when a rotation runs while approvals are
    /// outstanding.</summary>
    public int PauseCheckpoints { get; set; }
    public int PromptedVariablePayloads { get; set; }

    public int Total => Variables + SnapshotEntries + Settings + IdentityProviders
                        + OfflineDropFields + OutputVariables + PauseCheckpoints
                        + PromptedVariablePayloads;

    public string Summary =>
        $"{Variables} variables, {SnapshotEntries} snapshot entries across {Releases} releases, " +
        $"{Settings} settings secrets, {IdentityProviders} OIDC secrets, " +
        $"{OfflineDropFields} offline-drop targets, {OutputVariables} output variables, " +
        $"{PauseCheckpoints} pause checkpoints, {PromptedVariablePayloads} prompted-variable payloads";
}
