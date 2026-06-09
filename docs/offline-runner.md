# Offline Drop Runner

| | |
|---|---|
| **Version** | 1.0 |
| **Date** | 2026-06-08 |
| **Status** | Draft |
| **Tech** | .NET 10, KrakenDeploy.Agent |
| **Projects** | KrakenDeploy |

## Concept

An offline-drop deployment runs on an air-gapped target with **no server
connectivity**. The server bakes the fully-resolved `DeploymentPlan` (the same
one an online deployment dispatches to an agent) into an encrypted bundle; the
target runs it through the **same `DeploymentExecutor`** the online agent uses —
no second execution engine, so a process author gets identical semantics
(waves, output-variable feed-forward, run conditions, retries, timeouts,
Required gating).

Online is the primary path. The offline runner is intentionally thin: it does
what a single, self-contained process on one box can do. Server-orchestrated
step types (`Octopus.DeployRelease`, `Octopus.Manual`) are rejected at
bundle-generation time — they need a server and cannot run offline.

## Bundle layout

```
plan.enc                         AES-256-GCM(DeploymentPlan) — per-target bundle key
manifest.json + signature.bin    non-sensitive metadata, HMAC-signed
machine-info.json
packages/{id}/{version}/...       deployable packages
step-packages/{name}/{ver}/...    step-handler .kdeploy-step archives
runner/                           self-contained runner (optional — see below)
run.cmd / run.sh                  bootstrap
README.txt                        operator instructions
artifacts/, deployment-log.txt, deployment-result.json   runner output
```

## Keys (per target, provisioned once)

Two independent per-target keys live (encrypted with the server master key) on
`DeploymentTarget.OfflineDropConfig`:

- `HmacKeyEncrypted` — signs `manifest.json` (integrity). `POST /api/targets/{id}/generate-hmac-key`.
- `BundleKeyEncrypted` — AES-256-GCM key for `plan.enc` (confidentiality). `POST /api/targets/{id}/generate-bundle-key` returns the raw base64 key **once** — deliver it to the target operator out-of-band.

The operator places the bundle key in `bundle.key` next to the bootstrap, or in
the `KRAKEN_BUNDLE_KEY` environment variable.

## Staging the self-contained runner (no .NET on the target)

So the target needs no .NET runtime installed, the runner is published
**self-contained** (a build/release step — NOT done on the deployed server,
which has only the .NET runtime, no SDK and no source). `DropBundleService`
embeds the matching RID folder under `runner/` when present; otherwise the
bootstrap falls back to a `KrakenDeploy.Agent` on `PATH`.

Publish via the script (one folder per RID):

```powershell
./scripts/publish-offline-runner.ps1                       # win-x64 + linux-x64
./scripts/publish-offline-runner.ps1 -Rids win-x64
# → artifacts/offline-runner/<rid>/  (~110 MB each: exe + deps + .NET runtime)
```

Ship each `<rid>` folder with the server and place it at
`<DataPath>/offline-runner/<rid>/`; the embed is then automatic. The target RID
is derived from `DeploymentTarget.OperatingSystem` (`win-x64` for Windows,
otherwise `linux-x64`).

> **Folder publish only** — NOT single-file, NOT `PublishTrimmed`, NOT NativeAOT:
> the runner loads step-package handler assemblies at runtime via a collectible
> `AssemblyLoadContext`; trimming strips reflected types and AOT cannot JIT
> externally-loaded assemblies. The script enforces this.

> **Bundle size:** embedding the runner makes each bundle ≈110 MB. That is the
> price of a fully self-sufficient bundle.

### `OfflineDrop:EmbedRunner` (default `true`)

| Value | Behaviour |
|---|---|
| `true` (default) | Embed the staged runner for the target's RID (zero-install on target, ≈110 MB/bundle). Degrades gracefully if no runner is staged. |
| `false` | Never embed; bundles stay small (data only). The bootstrap uses a `KrakenDeploy.Agent` installed once on the target. Suits fleets where installing the runner once per machine beats shipping it per bundle. |

Both modes run the same bundle through the same runner; the toggle only decides
whether the runner travels inside the bundle. Edit it in the GUI on
**Configuration → Performance & retention** (`/configuration/performance`,
*Offline drop* card). It is a DB-backed `PerformanceSettings` knob (default
`true`); changes apply on the next offline drop.

## Running on the target

```
Windows : run.cmd
Linux   : ./run.sh
```

The bootstrap invokes `KrakenDeploy.Agent --run-offline-drop <bundleDir> --key-file bundle.key`.
Exit codes: `0` success, `1` a step failed (or a Required step aborted), `2`
setup error (wrong key / corrupt or missing plan).

After the run, the operator re-zips the bundle directory (now containing
`deployment-result.json`, `deployment-log.txt`, `artifacts/`) and uploads it on
the deployment page. The server's `OfflineResultService` reconciles status,
per-step outcomes, and output variables — the same rows an online deployment
produces.

## Limits (offline is secondary)

- No live progress; logs/results are reconciled only after upload.
- `Octopus.DeployRelease` / `Octopus.Manual` steps are refused at bundle-gen.
- Output variables flow **within** the run (cross-step feed-forward works), but
  there is no server round-trip mid-run.

## References

- `src/KrakenDeploy.Server.Data/Services/DropBundleService.cs` — bundle generation
- `src/KrakenDeploy.Agent/Offline/` — runner + bundle-backed ports
- `src/KrakenDeploy.Agent/Deployment/DeploymentExecutor.cs` — `ExecuteAsync(plan, orchestrateSteps)`
- `src/KrakenDeploy.Server.Data/Services/OfflineResultService.cs` — result ingestion
