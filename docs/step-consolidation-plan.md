# Step-Systems Consolidation — Packages Own Step Types

| | |
|---|---|
| **Version** | 1.1 |
| **Date** | 2026-07-31 |
| **Authors** | Domagoj Jugović, Claude (Fable 5; grill session 2026-07-30) |
| **Status** | Implemented (SC0–SC8; see §4 statuses) |
| **Technologies** | .NET 10, Blazor Server, Radzen, EF Core 10, PostgreSQL, gRPC, Hangfire, MSBuild |
| **Scope** | Step templates + step packages subsystems, picker/editor UI, catalog feeds, seeding, signing |

Companion docs: `docs/step-catalog-plan.md` (coverage tracker for built-in step types — stays),
`docs/step-packages.md` (package format reference — SC8 rewrites it), `docs/master-plan-2026-07-18.md`
(rows SC0/SC — this doc is their design source).

---

## 1. Problem (verified 2026-07-30)

Two subsystems grew orthogonally: **step templates** (M5 — author-time metadata, picker index,
Octopus community-library compat) and **step packages** (Phase D — distribution + the *only*
execution path since D-8.9 `0d63adf`). The UI half of "everything is a package" never happened:
form schemas live in hardcoded C# (`BuiltInStepSchemas`, ~52 types, 2 798 lines), picker
visibility lives in hand-seeded template rows (a23c691 added 46 of them), and executable truth
lives in package manifests. Three hand-maintained lists that must agree and don't.

Verified defects driving this plan:

1. **Empty-form Add** (a23c691): picking any of the 46 bare template cards builds the schema from
   `Template.Parameters` = `[]`; no fallback to `BuiltInStepSchemas` on that path
   (`StepFormDialog.razor` ~886 vs `ResolveSchemaAsync` ~1287).
2. **Seed gap**: only the 7 original steps projects are `ProjectReference`d by
   `KrakenDeploy.Server.csproj`; the 10 newer packages never reach `seed/step-packages` on a clean
   build/publish → `pin=null` → agent "Unknown step type". The `steps/**/bin` glob makes dev ≠ CI.
3. **Untrimmed stepTypes**: `Octopus.TentaclePackage, Kraken.DeployPackage` (space) is stored
   untrimmed; the resolver's `ILIKE ",kraken.deploypackage,"` never matches → type unpinnable.
4. **Dead schema plumbing**: no steps project ships `ui/ui-schema.json`; every
   `step_packages.UiSchemaJson` is NULL; the column is read only in the version-*switch* path
   (D-7.2), never on open.
5. **Package catalog 404**: default `KrakenDeploy/StepPackages` — the GitHub name is a squatted,
   inactive account; every hourly poll fails and is swallowed as a LogWarning. Template feed
   (`OctopusDeploy/Library`) is hardcoded consts with no off-switch. The `feeds.*` feature flags
   are documented as gating both polls but are wired to nothing.
6. **Unsigned built-ins**: every seeded archive carries `signature: "unsigned-dev-build"`; agents
   run them only with `StepPackages:AllowUnsignedLoads=true`.
7. **`RunOnServer` misroute**: any type except `Octopus.DeployRelease`/`Octopus.Manual` marked
   run-on-server lands in `ServerScriptStepRunner`, which reads `ScriptBody` regardless of type.

## 2. Locked decisions (grill session 2026-07-30, DJ)

| # | Decision |
|---|---|
| SD-1 | **Three-level model.** `step_packages` (versioned artifacts — the only thing agents execute) write through into a new **`step_types` registry** (one row per type id — sole authority for picker, schema, metadata). `step_templates` demote to **presets** (form + prefill over a registered base type; community/user only). |
| SD-2 | Community imports stay presets riding their base type's package; presets resolve **below** packages. Imports with unserved base types succeed but are **flagged unrunnable**, blocked at claim time with a clear reason, healed when the serving package installs. Preset-over-base-type *is* the extension mechanism — no new one. |
| SD-3 | Archive format: `ui/schemas/{typeId}.json`, one schema per type (legacy single `ui-schema.json` accepted for single-type packages during transition). |
| SD-4 | Manifest `stepTypes` entries become objects `{id, displayName, category, description, featured?}`; plain strings stay accepted (metadata falls back to package-level fields). |
| SD-5 | New `step_package_schemas` table keyed (package id, step type) — schemas are **per package version**. `step_packages.UiSchemaJson` is dropped. Editor renders the **pinned** version's schema on every open. |
| SD-6 | **Hard cut**: `BuiltInStepSchemas` converts once (generator) into per-type JSON files and is deleted; its tests become a schema lint. A step pinned to a pre-schema version renders the serving version's newest schema with a visible notice. |
| SD-7 | `Kraken.StepGroup` and `Octopus.DeployRelease` become `Source=System` registry rows with an **`ExecutionLocus`** field (`AgentPackage \| ServerRunner \| Structural`). Picker, claim guard and `WavePartitioner` read the registry — no special-cased type sets, and `RunOnServer` routing is gated by locus (fixes defect 7). |
| SD-8 | All `Source=BuiltIn` template rows (2 rich + 46 bare) are deleted; seeder blocks removed; the enum value retires. `StepTemplateDesigner` lives on as the **preset** designer. |
| SD-9 | **Picker redesign now**: source-sectioned grid (Featured / Installed types / Presets / Available to install), left category pane + search kept. Cards show package@version; inline Install-and-Add stays; unrunnable presets get a "requires X — not installed" badge with guarded Add; a **feed-health strip** shows last sync + last error per feed. |
| SD-10 | Seeding determinism: all 17 steps projects explicitly referenced; explicit item list replaces the bin glob. All packages get **minor version bumps** (schemas + metadata + trimmed stepTypes). |
| SD-11 | On seed of bumped versions, the seeder **auto-upgrades pins** of `Source=Preinstalled` packages (reusing bulk-upgrade machinery) and sweeps superseded built-in versions. |
| SD-12 | Feeds default **ON** with corrected owner. `KrakenDeploy` on GitHub is squatted → default becomes `DomagojJugovich/kraken-steps` (name changeable until SC6/SC7 land). Template feed gets config keys + `Enabled` parity and becomes **multi-feed** (list of owner/repo/branch/subdir sources). |
| SD-13 | **One community repo, two lanes**: GitHub Releases carry signed `.kdeploy-step` packages (PR → review → our CI packs, signs, publishes — community never publishes releases directly); a `step-templates/` tree carries preset JSONs (designer-export shape, light review). One CONTRIBUTING.md, two review bars. Both feeds (Octopus Library + ours) default from day one. |
| SD-14 | `feeds.*` feature flags get **wired into both jobs**: flag = runtime kill-switch, config `Enabled` = deployment posture. |
| SD-15 | **Signing in scope**: CI signs built-ins via the existing `SignKrakenStepPackage` target (key in CI secrets); prod ships `StepPackages:TrustedPublicKey`; `AllowUnsignedLoads=true` remains Development-only. Key-management UI (SigningKey entity) stays deferred (WP13 adjacency). |
| SD-16 | **Sequencing**: three P0 fixes land on `main` first (SC0); consolidation follows on branch `step_consolidation`. |

## 3. Target architecture

```
step_packages (artifacts, versioned)          ── the ONLY thing agents execute
  └─ step_package_schemas (per version, per type)
        │  install/uninstall writes through
        ▼
step_types (registry: ONE row per type id)    ── SSOT: picker, schema, metadata, ExecutionLocus
        ▲  FK BaseType
step_templates (presets over a type)          ── community/user starting points (CommunityLibrary | LocalImport | UserAuthored)
```

**Schema resolution (editor, every open):** pinned package version's schema →
serving package's newest schema (+ "pinned vX has no schema" notice) →
preset parameters (presets only) → error.

**Registry row**: `TypeId` (lowercased PK), `DisplayName`, `Category`, `Description`, `Featured`,
`ExecutionLocus`, `Source` (`Package | System`), `ServingPackageName` (+ cached serving version).
Maintained transactionally on package install/uninstall; System rows seeded by migration.

**Claim-time guard**: a step whose `StepType` has no registry row (or locus `AgentPackage` with no
installed serving package) refuses the claim with a reason — same UX channel as the F1 no-slot
reason.

## 4. Work packages

Sizes use master-plan conventions. SC0 goes to `main`; SC1–SC8 to `step_consolidation`.

**Statuses (2026-07-31):** SC0 ✅ `6cb372c` (merged `8e72c55`) · SC1 ✅ `8b1a4af`
· SC2 ✅ `966fbe9` · SC3+SC4-a ✅ `6b2678c` · SC4-b ✅ `bd5c310` · SC5 ✅
`63b91e6` · SC6 ✅ `69a156e` · SC7 ✅ `eac10b5` (repo creation + secrets =
operator actions, commands in the SC7 commit/summary) · SC8 ✅ (docs + resolver
/ template-service tests; the REST integration-test harness for the 21 step
endpoints is a RESIDUAL — no host-level test fixture exists repo-wide, so it
is test-infrastructure work beyond this plan's scope; tracked for WP14/test
debt). Implementation deviations from §4 as written: the `UiSchemaJson`
column drop moved from SC2 to SC4 (the editor's version-switch still read it);
the manifest gained `executionLocus` in SC4-b (Octopus.Manual = server) so
wave routing could retire its hardcoded set — packages own the locus truth.

### SC0 — P0 fixes on main (S)

1. `StepFormDialog` Add path: when `Template.Parameters` is empty, fall back to
   `ResolveSchemaAsync(Template.ActionType)` (keep template name/prefill).
2. Reference all 17 steps projects from `KrakenDeploy.Server.csproj`; replace the
   `steps/**/bin` glob with an explicit item list (build + publish targets).
3. `Trim()` step types at pack (`KrakenStepPackage.targets`) and install
   (`StepPackageService`); EF migration trims already-stored CSV values.

*Accept:* every picker card opens a populated form; clean `dotnet publish` seeds 17 packages;
`Kraken.DeployPackage` resolves a pin; CI = dev seed set.

### SC1 — Contracts & package format (M) — depends: SC0

- Manifest `stepTypes` objects per SD-4 (backward-compatible parse in `StepPackageManifest`).
- Pack target stages `ui/schemas/*.json`; per-type schema files required for all `steps/*` projects.
- One-off generator serializes `BuiltInStepSchemas` into the per-type JSON files (committed per
  project); `BuiltInStepSchemas` itself is NOT deleted yet (SC4 does).
- Schema lint test: every shipped schema passes `StepUiSchemaValidator`; `VisibleWhen` references
  existing fields; widgets valid; keys unique.
- Minor version bumps: `kraken.iis` 2.0.0→2.1.0, all others 1.0.0→1.1.0.

*Accept:* every archive contains per-type schemas + metadata; lint green; old-format archives
still install.

### SC2 — DB & registry (M) — depends: SC1

- New tables `step_types`, `step_package_schemas` (per §3); drop `step_packages.UiSchemaJson`.
- Migration: seed System rows (`Kraken.StepGroup` Structural, `Octopus.DeployRelease`
  ServerRunner); backfill registry from installed packages' `ManifestJson` (id-only metadata,
  healed on next seed); delete `Source=BuiltIn` template rows; `step_templates` gains
  `BaseTypeId` reference + unrunnable computed state.
- Per-account aware (runs against every account DB in multi-account mode).

*Accept:* fresh + existing DB migrate cleanly; registry reflects installed packages; System rows
present.

### SC3 — Install/seed write-through (M) — depends: SC2

- `UploadAsync` extracts per-type schemas + metadata → `step_package_schemas` + registry upsert;
  uninstall recomputes affected registry rows.
- Seeder: auto-upgrade `Preinstalled` pins to newly seeded versions (bulk-upgrade machinery),
  sweep superseded built-in versions (SD-11).
- `database setup` CLI gains package seeding + registry build (today it seeds only templates).

*Accept:* install/uninstall keeps registry consistent under concurrent readers; seed on an
existing DB upgrades pins and leaves no schema-less built-in pins behind.

### SC4 — Resolution, editor, execution guards (M) — depends: SC3

- Pin-aware schema resolution per §3 in `StepFormDialog` (open, edit, version-switch unify on one
  code path); **delete `BuiltInStepSchemas`** + its registrations; preset path keeps
  `StepTemplateSchemaAdapter`.
- Claim-time unrunnable guard; `WavePartitioner`/`DeploymentWorker` route by `ExecutionLocus`
  (retire the hardcoded `ServerOnlyStepTypes` set); `RunOnServer` honoured only where locus allows.
- Import paths (`ImportFromJson/Directory/OctopusApi`, community install) map `ActionType` through
  the registry and set the unrunnable flag per SD-2.

*Accept:* no reference to `BuiltInStepSchemas` remains; a step pinned to a pre-schema version
renders newest schema + notice; unserved-type claim refuses with reason; `Octopus.Email` marked
run-on-server is refused at author/claim time instead of dying in `ServerScriptStepRunner`.

### SC5 — Picker redesign (L) — depends: SC2 (data), SC4 (guards)

- Source-sectioned grid per SD-9; cards from registry + presets + catalog; Featured from manifest
  flag (initially `kraken.script`, `kraken.iis`, Step Group).
- Package@version badges, inline Install-and-Add, unrunnable badges, feed-health strip.
- `StepTemplateDesigner` re-targets presets: base-type dropdown sourced from registry; keeps
  Octopus-Library JSON export (that shape is the community `step-templates/` lane format).

*Accept:* every installed type is pickable and opens a populated form; presets and catalog
sections behave per SD-9; no card sources from `Source=BuiltIn` rows (they no longer exist).

### SC6 — Feeds & flags (M) — depends: SC2 (health storage)

- Template catalog: config keys (owner/repo/branch/subdir/enabled) + **multi-feed list**; defaults
  = `OctopusDeploy/Library` + `DomagojJugovich/kraken-steps` `step-templates/`.
- Package catalog default owner/repo → `DomagojJugovich/kraken-steps`.
- Wire `feeds.step-template-catalog` / `feeds.step-package-catalog` flags into both jobs (SD-14).
- Persist last-sync/last-error per feed (settings document or dedicated rows) — feeds the SC5
  health strip; surface refresh failures in `/step-packages` and the community browser.

*Accept:* both flags actually stop polling; feed failures visible in UI with timestamps; defaults
point at repos that exist.

### SC7 — Community repo & signing (M) — depends: SC1 (format frozen)

- Create `DomagojJugovich/kraken-steps`: Releases lane (signed `.kdeploy-step` + manifest
  ```json block + `SHA-256:` line per `StepPackageCatalogService` conventions) and
  `step-templates/` tree lane; CONTRIBUTING.md with the two review bars (SD-13).
- Publish workflow: PR → review → CI packs + signs (`SignKrakenStepPackage`, key in CI secrets) →
  Release. Community never publishes releases directly.
- Server repo CI: sign built-in seed archives; prod config ships `TrustedPublicKey`;
  `AllowUnsignedLoads` Development-only.

*Accept:* catalog poll discovers a real release end-to-end (refresh → install → agent load with
signature verification); production compose boots with signed built-ins and
`AllowUnsignedLoads=false`.

### SC8 — Docs & test-gap closure (M) — depends: SC4–SC7

- `docs/step-packages.md` v2 (per-type schemas, registry, feeds, signing); fix stale
  `docs/architecture.md` sections (in-DI handler chain deleted by D-8.9, extension-points table,
  picker description); update `docs/step-catalog-plan.md` coverage notes.
- Tests: registry write-through (install/uninstall/seed), pin-aware resolution,
  `StepTemplateService` CRUD + imports, template catalog refresh (Git Trees diff), REST
  integration tests for the step-templates/step-packages endpoints (currently zero).

## 5. Migration notes

- Pre-GA: no customer DBs; dev DBs (`krakendeploy_dev`, `kraken_acct_*`) migrate in place.
- Order on an existing DB: migrate (SC2) → seed bumped packages (SC3) → pins auto-upgrade →
  registry healed with full metadata. Old schema-less versions swept.
- Rollback: SC2 migration is expand-only until the `UiSchemaJson` drop; keep the drop in a
  separate migration so restore-point rollback stays trivial pre-merge.

## 6. Out of scope

- Key-management UI / `SigningKey` entity (deferred; WP13 adjacency).
- Topology-aware feed defaults (BG1's `Deployment:Topology` — revisit after BG1 lands).
- Options-provider mechanism for server-data-backed widgets (WP3-a residual, unchanged).
- Picker/editor visual-theme work beyond SD-9.

## 7. References

- Code: `src/KrakenDeploy.Contracts/Steps/BuiltInStepSchemas.cs`,
  `src/KrakenDeploy.Server.Data/Services/{StepPackageService,StepPackageResolver,StepPackageCatalogService,StepTemplateService,StepTemplateCatalogService,BuiltInStepTemplates,BuiltInStepPackageSeeder}.cs`,
  `src/KrakenDeploy.Server/Components/Dialogs/{ChooseStepTemplateDialog,StepFormDialog}.razor`,
  `src/KrakenDeploy.Server.Transport/{WavePartitioner,DeploymentWorker,ServerScriptStepRunner,GrpcStepPackageDeliveryService}.cs`,
  `steps/KrakenStepPackage.targets`, `src/KrakenDeploy.Server/KrakenDeploy.Server.csproj`.
- History: templates M5 (`dead079`…`ac3a46c`), packages Phase C/D (`be7fefb`…`666c726`),
  handler-path retirement D-8.9 (`0d63adf`), 45-type expansion (`a23c691`).
- GitHub facts (2026-07-30): `KrakenDeploy` account squatted/inactive since 2016;
  `api.github.com/repos/KrakenDeploy/StepPackages` → 404.
