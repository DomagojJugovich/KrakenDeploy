# Extended Tag Sets — Design & Implementation Plan

| | |
|---|---|
| **Version** | 1.0 |
| **Date** | 2026-07-08 |
| **Authors** | Domagoj Jugović, Claude |
| **Status** | Approved |
| **Technologies** | .NET 10, EF Core 10, PostgreSQL, Blazor Server (Radzen 11) |
| **Projects** | KrakenDeploy.Server.Core, KrakenDeploy.Server.Data, KrakenDeploy.Server, KrakenDeploy.Server.Transport |

## Motivation

The current tag model is tenant-owned and target-only: `TagSet.TenantId` is required
(every tag set belongs to exactly one tenant) and tags can be applied only to
deployment targets (`target_tenant_tags` M2M). Octopus's extended tag sets
([blog](https://octopus.com/blog/extended-tag-sets)) generalize this: tag sets are
Space-level assets with a **Scope** (which entity kinds they apply to) and a
**Type** (selection cardinality). This unblocks the deploy-dialog tag picker,
future tag-based filters, freeze-by-tag, and variable scoping.

Breaking changes are acceptable — the product is not in production. The migration
is destructive (drop + recreate); `seed-demo` re-creates demo data.

## Data model

### `TagSet` (Space-level; `AuditableEntity, ISpaceScoped`)

| Field | Type | Notes |
|---|---|---|
| `SpaceId` | Guid | space cage |
| `Name` | string | unique `(SpaceId, Name)` |
| `Description` | string? | |
| `SortOrder` | int | ordering across sets |
| `Type` | `TagSetType` | `MultiSelect` (default) / `SingleSelect` / `FreeText` |
| `Scopes` | `List<TaggableEntityKind>` | which entity kinds the set applies to; multi-scope allowed |
| `Tags` | collection | cascade delete |

`TenantId` ownership is **removed**.

### `Tag` (renamed from `TenantTag`; `AuditableEntity, ISpaceScoped`)

| Field | Type | Notes |
|---|---|---|
| `TagSetId` | Guid | FK, cascade |
| `Name` | string | unique `(TagSetId, Name)` |
| `Color` | string? | UI dot |
| `Description` | string? | new — per-tag description |
| `SortOrder` | int | new — manual reorder |

### `TagApplication` (new; `AuditableEntity, ISpaceScoped`) — unified polymorphic link

| Field | Type | Notes |
|---|---|---|
| `TagSetId` | Guid | FK, cascade |
| `TagId` | Guid? | set for Select types; FK, cascade |
| `EntityKind` | `TaggableEntityKind` | `Tenant / Project / Environment / Runbook / DeploymentTarget` |
| `EntityId` | Guid | no FK (polymorphic) — cleanup via interceptor |
| `FreeTextValue` | string? | set for FreeText type |
| `SetType` | `TagSetType` | denormalized from the set, stamped by the service — enables the partial unique index |

**Indexes**
- unique `(TagSetId, EntityKind, EntityId, TagId)` — no duplicate tag on an entity
- partial unique `(TagSetId, EntityKind, EntityId) WHERE set_type IN (SingleSelect, FreeText)` — cardinality is DB-enforced
- `(EntityKind, EntityId)` — "tags of this entity" lookup
- `(TagId)` — "entities with this tag" lookup

**Orphan cleanup**: `TagApplicationCleanupInterceptor` (SaveChanges interceptor, same
pattern as `AuditLogInterceptor`) — when a taggable entity is deleted, its
applications are removed in the same save/transaction.

## Semantics

- Applying a tag to an entity kind not in the set's `Scopes` → service rejects.
- **Scope removal** with existing applications of that kind → service refuses unless
  `force: true`; UI shows a confirm dialog with the affected count, then cascades.
- **Type change** → blocked while applications violate the new cardinality;
  Select ↔ FreeText conversion blocked while *any* applications exist.
- **References are Guids everywhere.** Canonical `"TagSetName/TagName"` strings are a
  display/Octopus-parity format only (`Octopus.Deployment.Tenant.Tags`).
- FreeText: exactly one arbitrary value per set per entity; empty/null clears it.

## Consumer changes (v1)

| Consumer | Action |
|---|---|
| `DeploymentFreeze.TenantTagCanonicalNames` | reshape → `List<Guid> TagIds`; **matching stays dormant** (worker keeps passing null) |
| `RoleAssignment.TenantTagIds`, `PermissionScope.TenantTagId` | rename → `TagIds` / `TagId`; stays dormant |
| `Octopus.Deployment.Tenant.Tags` system variable | **wired** — canonical strings of the deployment tenant's applied tags |
| `target_tenant_tags` M2M, `DeploymentTarget.TenantTags`, `Tenant.TagSets` | removed |
| `TenantService` tag members | removed → new `TagService` |
| `/api/tenants/{id}/tag-sets` endpoint block | removed → `/api/tag-sets` CRUD + per-entity application endpoints (entity-Edit permission checked in-handler per kind) |
| TenantDetail tag-set section | removed → `EntityTagEditor` |
| Octopus importer, `seed-demo` | verified/updated |

## UI (v1)

- **Library → Tag Sets**: list page + full-page editor (Name, Description, Scope
  multi-select, Type radio, tags with reorder/color/description) — mirrors the
  Octopus screens; reorder via up/down buttons (LifecycleDetail `MovePhase` pattern).
- **`EntityTagEditor` shared component** (`Kind`, `EntityId`, `EditPermission`
  parameters) on all five kinds: Tenant, Target, Project, Environment, Runbook.
  Where a kind has no detail page, an "Edit tags" row-action dialog hosts the same
  component. Renders per set by Type: chip multi-select / single dropdown / textbox.
- Permissions: applying tags = the entity's `*Edit` permission (re-checked via
  `UiActionGuard`); managing sets = existing `TagSet*` atoms.

## Explicitly out of scope (v1)

- Tag-based filters (dashboard, environments, deployments list)
- Freeze-by-tag matching (field reshaped, logic dormant)
- Runbook triggers by tag
- License/variable scoping by tag

## Known limitations

- **Type-change race (accepted).** A `TagSet` Type change (e.g. MultiSelect →
  SingleSelect) concurrent with a tag apply on the same set can, under READ
  COMMITTED with no row lock, persist one stale-`SetType` row that the partial
  unique index (`WHERE set_type IN (1,2)`) can't see — a soft cardinality
  violation that self-repairs on the next save of that entity's tags. Not
  fixed: the domain carries no optimistic-concurrency token anywhere, and a
  `SELECT … FOR UPDATE` on `tag_sets` would be inconsistent with the rest of
  the codebase. Window is milliseconds against a rare admin action.

## Follow-up

The paused deploy-dialog work resumes on top of this model: Target-scoped tag sets
drive an any-match filter narrowing the target multi-select; tenant remains a plain
dropdown.

## History

| Version | Date | Change |
|---|---|---|
| 1.0 | 2026-07-08 | Initial approved plan (grill-me session) |
