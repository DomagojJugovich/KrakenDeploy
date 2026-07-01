# KrakenDeploy — Converting a Single-Instance Install to a SaaS Account

| | |
|---|---|
| **Version** | 0.1 |
| **Date** | 2026-06-30 |
| **Authors** | Domagoj Jugović (LAUS CC) — drafted with Claude Code |
| **Status** | `Draft` |
| **Technologies** | .NET 10, PostgreSQL, EF Core 10 |
| **Projects** | `KrakenDeploy.Server`, `KrakenDeploy.ControlPlane`, `KrakenDeploy.Server.Data` |

## Purpose

How to migrate an existing **single-instance** KrakenDeploy install (one `KrakenDb`, a flat `{DataPath}/packages` + `{DataPath}/artifacts` file tree) into a **multi-account** SaaS deployment as one business account — including the **file relocation** into that account's segregated slice.

## Why this is a runbook, not a one-click command

There is **no automated single-instance→account conversion flow** in the codebase, by design. `AccountProvisioner.ProvisionAsync` — the only path that creates a `BusinessAccount` — always creates a **fresh, empty** tenant DB and seeds it (Default Space + RBAC + first admin). It has no "adopt an existing populated DB" branch, and `TenantInitializer.SeedAsync` assumes a blank DB. Building an in-place adoption path is net-new design; conversion is rare and operator-supervised, so a runbook is the right tool.

The good news: the pieces already exist. **Per-account backup/restore does the heavy lifting** — the per-account `restore` command restores a bundle's database into the target tenant DB *and copies the bundle's `data/` into that account's file slice* (`{DataPath}/accounts/{accountId}/`). So the file relocation is not a separate manual `mv`; it falls out of `restore`.

## Procedure

Prereqs: the SaaS deployment is up (`MultiAccount:Enabled=true`, catalog DB + `ConnectionStrings:Catalog`, shard admin connection), `pg_dump`/`psql` on the box, and a maintenance window (the single-instance app should be stopped so the backup is consistent).

1. **Back up the single-instance install.** From the single-instance install (or its binaries) run the CLI backup, or use the UI "Backup now". This produces a bundle `kraken-backup-<ts>/` containing `database.sql`, `data/` (the whole flat `{DataPath}`), and `manifest.json`. A single-instance bundle has **no account stamp** (`manifest.Account == null`).

2. **Provision the target account.** Create the account through the control plane / provisioning (`AccountProvisioner.ProvisionAsync`), which creates a fresh tenant DB `kraken_acct_<subdomain>`, a catalog row, and the connection secret. Note the resulting **subdomain**.

3. **Restore the bundle into the account.** Run the per-account restore:
   ```
   restore --from <path>/kraken-backup-<ts> --account <subdomain>
   ```
   This resolves the account's tenant DB from the catalog, `psql`-restores `database.sql` into it (the `--clean` dump overwrites the fresh seed from step 2 — expected), and **copies `data/` into `{DataPath}/accounts/{accountId}/`** — the exact layout `LocalPackageStore`/`LocalArtifactStore` read from. Because the bundle has no account stamp, restore prints a one-line warning (it cannot verify the bundle's origin) and proceeds.

4. **Verify.** Sign in at `<subdomain>.<base>`; confirm projects/deployments/targets are present and that package/artifact downloads work (the scoped stores resolve under `{DataPath}/accounts/{accountId}/`).

## Caveats

- **File relocation is handled by `restore`** (step 3) — there is no separate `mv` step. The relocation target (`{DataPath}/accounts/{accountId}/`) matches the per-account store `RootPath`, keyed by the immutable account id (not the subdomain).
- **Platform-global material is over-copied, harmlessly.** A single-instance bundle's `data/` includes the Data Protection key ring and `license.key` (they live at the `{DataPath}` root single-instance). Restore copies them under the account slice too, where they are simply **ignored** — the platform reads the key ring + license from the `{DataPath}` root, not the slice. Optionally delete `{DataPath}/accounts/{accountId}/dataprotection-keys` and `license.key` after conversion to avoid clutter. Do **not** treat the slice copies as the live key ring.
- **Seeding is overwritten, by design.** Step 2 seeds a Default Space + RBAC + first admin; step 3's `--clean` restore drops and recreates those from the single-instance data. Identity (users) comes from the single-instance DB, so existing logins carry over.
- **One account per single-instance DB.** This converts a whole single-instance install into exactly one account. Splitting one install across multiple accounts is not supported (the single-instance DB has one Space-set; per-account isolation is the DB boundary).
- **Version match.** `restore` refuses a bundle whose `ServerVersion` differs from the running server — take the backup and restore on the **same** server version (or downgrade the binary to match before restoring).

## References

- `docs/saas-multi-account-architecture.md` (§6 tenancy, §11 file/store layout, §16 components)
- `docs/saas-phase3-account-awareness.md` — per-account file store + backup/restore work
- `src/KrakenDeploy.Server/Commands/RestoreCommands.cs` — the `--account` restore that relocates the file slice
- `src/KrakenDeploy.Server.Data/Services/BackupEngine.cs` — bundle shape + account stamp
- `src/KrakenDeploy.ControlPlane/Provisioning/AccountProvisioner.cs` — provisioning (fresh-DB only)

## History

| Version | Date | Author | Change |
|---|---|---|---|
| 0.1 | 2026-06-30 | Domagoj Jugović | Initial runbook: backup → provision → per-account restore (restore relocates the file slice); caveats on over-copied platform-global material + seeding overwrite. |
