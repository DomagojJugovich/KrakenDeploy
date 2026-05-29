# KrakenDeploy — Database ER Diagram

| | |
|---|---|
| **Status** | Approved |
| **Version** | 1.0 |
| **Last updated** | 2026-05-29 |
| **Applies to** | `KrakenDbContext` (PostgreSQL, snake_case), ~61 tables |
| **Source of truth** | `src/KrakenDeploy.Server.Data/Migrations/KrakenDbContextModelSnapshot.cs` |

Visual ER diagram derived from the EF Core model snapshot. Diagrams are split by
subsystem for readability; together they cover all 61 foreign keys. Mermaid
renders inline on GitHub and in VS Code (Markdown Preview Mermaid Support).

## Conventions

- **Cardinality:** `||--o{` = required parent → many children (FK `NOT NULL`);
  `|o--o{` = optional parent → many children (FK nullable, `ON DELETE SET NULL`);
  `||--||` = one-to-one; `}o--o{` = many-to-many via a join table.
- **Edge label** = the FK column (or the join table, for m2m).
- **Space scoping:** Many tables carry a `space_id` filter column WITHOUT a
  DB-level FK to `spaces` (the EF global query filter enforces isolation, not a
  constraint). Only the tables with an actual FK are drawn against `spaces` in
  Diagram 1. The filter-only tables are listed under §8.
- Columns/types/indexes are not shown here — generate full DDL with
  `dotnet ef migrations script --idempotent` (see §9).

## 1. Space-scoped aggregate roots

Tables that have a real FK to `spaces` (cascade is `RESTRICT` — a Space can't be
deleted while it owns objects).

```mermaid
erDiagram
    spaces ||--o{ project_groups : space_id
    spaces ||--o{ projects : space_id
    spaces ||--o{ environments : space_id
    spaces ||--o{ deployment_targets : space_id
    spaces ||--o{ tenants : space_id
    spaces ||--o{ lifecycles : space_id
    spaces ||--o{ channels : space_id
    spaces ||--o{ releases : space_id
    spaces ||--o{ deployments : space_id
    spaces ||--o{ variable_sets : space_id
    spaces ||--o{ packages : space_id
    spaces ||--o{ step_templates : space_id
    spaces ||--o{ tag_sets : space_id
    spaces ||--o{ runbooks : space_id
```

## 2. Project, release & deployment domain

```mermaid
erDiagram
    project_groups |o--o{ projects : project_group_id
    lifecycles |o--o{ projects : lifecycle_id
    projects ||--o{ channels : project_id
    lifecycles |o--o{ channels : lifecycle_id
    projects ||--o{ releases : project_id
    channels |o--o{ releases : channel_id
    releases ||--o{ deployments : release_id
    environments ||--o{ deployments : environment_id
    deployment_targets |o--o{ deployments : target_id
    tenants |o--o{ deployments : tenant_id
    deployments |o--o{ deployments : parent_deployment_id
    deployments ||--o{ deployment_artifacts : deployment_id
    deployments ||--o{ deployment_log_entries : deployment_id
    deployments ||--o{ deployment_output_variables : deployment_id
    deployments ||--o{ deployment_step_outcomes : deployment_id
    deployments ||--o{ deployment_target_assignments : deployment_id
    deployment_targets ||--o{ deployment_target_assignments : target_id
```

## 3. Deployment process & variables

```mermaid
erDiagram
    projects ||--|| deployment_processes : project_id
    deployment_processes ||--o{ deployment_steps : process_id
    deployment_steps |o--o{ deployment_steps : parent_step_id
    projects ||--|| variable_sets : project_id
    variable_sets ||--o{ variables : set_id
```

## 4. Runbooks

```mermaid
erDiagram
    projects ||--o{ runbooks : project_id
    runbooks ||--|| runbook_processes : runbook_id
    runbook_processes ||--o{ runbook_steps : process_id
    runbook_steps |o--o{ runbook_steps : parent_step_id
    runbooks ||--o{ runbook_runs : runbook_id
    environments ||--o{ runbook_runs : environment_id
    deployment_targets |o--o{ runbook_runs : target_id
    tenants |o--o{ runbook_runs : tenant_id
    runbook_runs ||--o{ runbook_run_log_entries : runbook_run_id
```

## 5. Tenants & tags

```mermaid
erDiagram
    tenants ||--o{ tag_sets : tenant_id
    tag_sets ||--o{ tenant_tags : tag_set_id
    projects }o--o{ tenants : project_tenants
    deployment_targets }o--o{ tenant_tags : target_tenant_tags
```

## 6. Identity, teams & RBAC (M10)

```mermaid
erDiagram
    users ||--o{ user_claims : user_id
    users ||--o{ user_logins : user_id
    users ||--o{ user_tokens : user_id
    teams ||--o{ team_members : team_id
    teams ||--o{ team_external_groups : team_id
    identity_providers |o--o{ team_external_groups : identity_provider_id
    teams |o--o{ identity_providers : default_team_id
    teams ||--o{ role_assignments : team_id
    roles ||--o{ role_assignments : role_id
```

> `team_members.user_id` and `role_assignments` scope columns
> (`space_id`/`project_id`/`environment_id`/`tenant_id`) are stored without FK
> constraints — they're resolved by the permission evaluator, not the DB.

## 7. AI (M11)

```mermaid
erDiagram
    adhoc_sessions ||--o{ adhoc_iterations : session_id
```

The other AI tables — `space_ai_settings` (one row per Space),
`ai_call_logs`, `deployment_diagnoses` — and `adhoc_sessions` itself are
Space-scoped by the `space_id` filter column only (no FK; see §8).

## 8. Space-scoped (filter-only) & standalone/config tables

No outbound FK in the model; `space_id` (where present) is a query-filter column,
not a constraint.

- **Space-scoped (filter only):** `adhoc_sessions`, `space_ai_settings`,
  `ai_call_logs`, `deployment_diagnoses`, `audit_entries`, `deployment_freezes`,
  `event_subscriptions`, `backup_runs`.
- **Server-level config / singletons:** `maintenance_settings`,
  `performance_settings`, `smtp_settings`, `backup_settings`, `feature_flags`.
- **Catalog / queue / delivery:** `step_packages`, `step_package_catalog`,
  `step_template_catalog`, `email_digest_outbox`, `subscription_deliveries`,
  `subscription_poller_state`.

## 9. Regenerate full DDL

```powershell
dotnet ef migrations script --idempotent `
  --project src/KrakenDeploy.Server.Data `
  --startup-project src/KrakenDeploy.Server.Data `
  --output docs/schema.sql
```

## References

- `src/KrakenDeploy.Server.Data/Migrations/KrakenDbContextModelSnapshot.cs` — authoritative model
- `src/KrakenDeploy.Server.Data/Configurations/*.cs` — per-entity EF configuration
- `docs/adhoc-actions.md` — the AI ad-hoc subsystem (M11.E)
