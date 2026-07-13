using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class HardenSpaceScopeCompositeFks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_adhoc_iterations_adhoc_sessions_session_id",
                table: "adhoc_iterations");

            migrationBuilder.DropForeignKey(
                name: "fk_adhoc_iterations_spaces_space_id",
                table: "adhoc_iterations");

            migrationBuilder.DropForeignKey(
                name: "fk_channels_lifecycles_lifecycle_id",
                table: "channels");

            migrationBuilder.DropForeignKey(
                name: "fk_channels_projects_project_id",
                table: "channels");

            migrationBuilder.DropForeignKey(
                name: "fk_channels_spaces_space_id",
                table: "channels");

            migrationBuilder.DropForeignKey(
                name: "fk_deployment_diagnoses_server_tasks_deployment_id",
                table: "deployment_diagnoses");

            migrationBuilder.DropForeignKey(
                name: "fk_deployment_diagnoses_spaces_space_id",
                table: "deployment_diagnoses");

            migrationBuilder.DropForeignKey(
                name: "fk_process_steps_process_steps_parent_step_id",
                table: "process_steps");

            migrationBuilder.DropForeignKey(
                name: "fk_process_steps_processes_process_id",
                table: "process_steps");

            migrationBuilder.DropForeignKey(
                name: "fk_process_steps_spaces_space_id",
                table: "process_steps");

            migrationBuilder.DropForeignKey(
                name: "fk_project_tenants_projects_projects_id",
                table: "project_tenants");

            migrationBuilder.DropForeignKey(
                name: "fk_project_tenants_tenants_tenants_id",
                table: "project_tenants");

            migrationBuilder.DropForeignKey(
                name: "fk_project_variable_set_links_projects_project_id",
                table: "project_variable_set_links");

            migrationBuilder.DropForeignKey(
                name: "fk_project_variable_set_links_variable_sets_variable_set_id",
                table: "project_variable_set_links");

            migrationBuilder.DropForeignKey(
                name: "fk_projects_lifecycles_lifecycle_id",
                table: "projects");

            migrationBuilder.DropForeignKey(
                name: "fk_projects_project_groups_project_group_id",
                table: "projects");

            migrationBuilder.DropForeignKey(
                name: "fk_releases_channels_channel_id",
                table: "releases");

            migrationBuilder.DropForeignKey(
                name: "fk_releases_projects_project_id",
                table: "releases");

            migrationBuilder.DropForeignKey(
                name: "fk_releases_spaces_space_id",
                table: "releases");

            migrationBuilder.DropForeignKey(
                name: "fk_runbooks_projects_project_id",
                table: "runbooks");

            migrationBuilder.DropForeignKey(
                name: "fk_runbooks_spaces_space_id",
                table: "runbooks");

            migrationBuilder.DropForeignKey(
                name: "fk_server_tasks_environments_environment_id",
                table: "server_tasks");

            migrationBuilder.DropForeignKey(
                name: "fk_server_tasks_releases_release_id",
                table: "server_tasks");

            migrationBuilder.DropForeignKey(
                name: "fk_server_tasks_runbooks_runbook_id",
                table: "server_tasks");

            migrationBuilder.DropForeignKey(
                name: "fk_server_tasks_server_tasks_parent_task_id",
                table: "server_tasks");

            migrationBuilder.DropForeignKey(
                name: "fk_server_tasks_tenants_tenant_id",
                table: "server_tasks");

            migrationBuilder.DropForeignKey(
                name: "fk_tag_applications_spaces_space_id",
                table: "tag_applications");

            migrationBuilder.DropForeignKey(
                name: "fk_tag_applications_tag_sets_tag_set_id",
                table: "tag_applications");

            migrationBuilder.DropForeignKey(
                name: "fk_tag_applications_tags_tag_id",
                table: "tag_applications");

            migrationBuilder.DropForeignKey(
                name: "fk_tags_spaces_space_id",
                table: "tags");

            migrationBuilder.DropForeignKey(
                name: "fk_tags_tag_sets_tag_set_id",
                table: "tags");

            migrationBuilder.DropForeignKey(
                name: "fk_target_environments_deployment_targets_deployment_target_id",
                table: "target_environments");

            migrationBuilder.DropForeignKey(
                name: "fk_target_environments_environments_environments_id",
                table: "target_environments");

            migrationBuilder.DropForeignKey(
                name: "fk_target_tenants_deployment_targets_deployment_target_id",
                table: "target_tenants");

            migrationBuilder.DropForeignKey(
                name: "fk_target_tenants_tenants_tenants_id",
                table: "target_tenants");

            migrationBuilder.DropForeignKey(
                name: "fk_task_artifacts_server_tasks_task_id",
                table: "task_artifacts");

            migrationBuilder.DropForeignKey(
                name: "fk_task_artifacts_spaces_space_id",
                table: "task_artifacts");

            migrationBuilder.DropForeignKey(
                name: "fk_task_output_variables_server_tasks_task_id",
                table: "task_output_variables");

            migrationBuilder.DropForeignKey(
                name: "fk_task_output_variables_spaces_space_id",
                table: "task_output_variables");

            migrationBuilder.DropForeignKey(
                name: "fk_task_step_outcomes_deployment_targets_target_id",
                table: "task_step_outcomes");

            migrationBuilder.DropForeignKey(
                name: "fk_task_step_outcomes_server_tasks_task_id",
                table: "task_step_outcomes");

            migrationBuilder.DropForeignKey(
                name: "fk_task_step_outcomes_spaces_space_id",
                table: "task_step_outcomes");

            migrationBuilder.DropForeignKey(
                name: "fk_task_target_assignments_deployment_targets_target_id",
                table: "task_target_assignments");

            migrationBuilder.DropForeignKey(
                name: "fk_task_target_assignments_server_tasks_task_id",
                table: "task_target_assignments");

            migrationBuilder.DropForeignKey(
                name: "fk_tenants_variable_sets_variable_set_id",
                table: "tenants");

            migrationBuilder.DropForeignKey(
                name: "fk_variable_sets_projects_project_id",
                table: "variable_sets");

            migrationBuilder.DropForeignKey(
                name: "fk_variables_spaces_space_id",
                table: "variables");

            migrationBuilder.DropForeignKey(
                name: "fk_variables_variable_sets_set_id",
                table: "variables");

            migrationBuilder.DropIndex(
                name: "ix_variables_space_id",
                table: "variables");

            migrationBuilder.DropIndex(
                name: "ix_task_step_outcomes_space_id",
                table: "task_step_outcomes");

            migrationBuilder.DropIndex(
                name: "ix_task_step_outcomes_target_id",
                table: "task_step_outcomes");

            migrationBuilder.DropIndex(
                name: "ix_task_output_variables_space_id",
                table: "task_output_variables");

            migrationBuilder.DropIndex(
                name: "ix_task_artifacts_space_id",
                table: "task_artifacts");

            migrationBuilder.DropPrimaryKey(
                name: "pk_target_tenants",
                table: "target_tenants");

            migrationBuilder.DropIndex(
                name: "ix_target_tenants_tenants_id",
                table: "target_tenants");

            migrationBuilder.DropPrimaryKey(
                name: "pk_target_environments",
                table: "target_environments");

            migrationBuilder.DropIndex(
                name: "ix_target_environments_environments_id",
                table: "target_environments");

            migrationBuilder.DropIndex(
                name: "ix_tags_space_id",
                table: "tags");

            migrationBuilder.DropIndex(
                name: "ix_tag_applications_space_id",
                table: "tag_applications");

            migrationBuilder.DropIndex(
                name: "ix_server_tasks_environment_id",
                table: "server_tasks");

            migrationBuilder.DropIndex(
                name: "ix_server_tasks_tenant_id",
                table: "server_tasks");

            migrationBuilder.DropIndex(
                name: "ix_runbooks_space_id",
                table: "runbooks");

            migrationBuilder.DropIndex(
                name: "ix_releases_channel_id",
                table: "releases");

            migrationBuilder.DropIndex(
                name: "ix_releases_space_id",
                table: "releases");

            migrationBuilder.DropIndex(
                name: "ix_projects_lifecycle_id",
                table: "projects");

            migrationBuilder.DropPrimaryKey(
                name: "pk_project_tenants",
                table: "project_tenants");

            migrationBuilder.DropIndex(
                name: "ix_project_tenants_tenants_id",
                table: "project_tenants");

            migrationBuilder.DropIndex(
                name: "ix_process_steps_space_id",
                table: "process_steps");

            migrationBuilder.DropIndex(
                name: "ix_deployment_diagnoses_space_id",
                table: "deployment_diagnoses");

            migrationBuilder.DropIndex(
                name: "ix_channels_lifecycle_id",
                table: "channels");

            migrationBuilder.DropIndex(
                name: "ix_channels_space_id",
                table: "channels");

            migrationBuilder.DropIndex(
                name: "ix_adhoc_iterations_space_id",
                table: "adhoc_iterations");

            // ── Join-table space_id setup (hand-written to preserve data) ────────
            // EF's scaffolder mis-generated the implicit-join conversions as blind
            // column renames (e.g. tenants_id -> space_id), which would corrupt the
            // data. Instead: add the correctly-named columns, backfill space_id from
            // the parent (both ends of each join share one Space), drop old columns.
            // No default values — matches the model snapshot. On an empty DB these
            // run as no-op DDL. Old PKs/indexes/FKs on the dropped columns were
            // already removed by the operations above.

            // target_tenants: (deployment_target_id, tenants_id) -> (space_id, target_id, tenant_id)
            migrationBuilder.Sql(@"
                ALTER TABLE target_tenants
                    ADD COLUMN space_id uuid,
                    ADD COLUMN target_id uuid,
                    ADD COLUMN tenant_id uuid;
                UPDATE target_tenants tt
                    SET target_id = tt.deployment_target_id,
                        tenant_id = tt.tenants_id,
                        space_id  = dt.space_id
                    FROM deployment_targets dt
                    WHERE dt.id = tt.deployment_target_id;
                ALTER TABLE target_tenants
                    ALTER COLUMN space_id SET NOT NULL,
                    ALTER COLUMN target_id SET NOT NULL,
                    ALTER COLUMN tenant_id SET NOT NULL,
                    DROP COLUMN deployment_target_id,
                    DROP COLUMN tenants_id;");

            // target_environments: (deployment_target_id, environments_id) -> (space_id, target_id, environment_id)
            migrationBuilder.Sql(@"
                ALTER TABLE target_environments
                    ADD COLUMN space_id uuid,
                    ADD COLUMN target_id uuid,
                    ADD COLUMN environment_id uuid;
                UPDATE target_environments te
                    SET target_id = te.deployment_target_id,
                        environment_id = te.environments_id,
                        space_id  = dt.space_id
                    FROM deployment_targets dt
                    WHERE dt.id = te.deployment_target_id;
                ALTER TABLE target_environments
                    ALTER COLUMN space_id SET NOT NULL,
                    ALTER COLUMN target_id SET NOT NULL,
                    ALTER COLUMN environment_id SET NOT NULL,
                    DROP COLUMN deployment_target_id,
                    DROP COLUMN environments_id;");

            // project_tenants: (projects_id, tenants_id) -> (space_id, project_id, tenant_id)
            migrationBuilder.Sql(@"
                ALTER TABLE project_tenants
                    ADD COLUMN space_id uuid,
                    ADD COLUMN project_id uuid,
                    ADD COLUMN tenant_id uuid;
                UPDATE project_tenants pt
                    SET project_id = pt.projects_id,
                        tenant_id = pt.tenants_id,
                        space_id  = p.space_id
                    FROM projects p
                    WHERE p.id = pt.projects_id;
                ALTER TABLE project_tenants
                    ALTER COLUMN space_id SET NOT NULL,
                    ALTER COLUMN project_id SET NOT NULL,
                    ALTER COLUMN tenant_id SET NOT NULL,
                    DROP COLUMN projects_id,
                    DROP COLUMN tenants_id;");

            // task_target_assignments: add space_id, backfill from the owning task.
            migrationBuilder.Sql(@"
                ALTER TABLE task_target_assignments ADD COLUMN space_id uuid;
                UPDATE task_target_assignments a
                    SET space_id = st.space_id
                    FROM server_tasks st
                    WHERE st.id = a.task_id;
                ALTER TABLE task_target_assignments ALTER COLUMN space_id SET NOT NULL;");

            // project_variable_set_links: add space_id, backfill from the owning project.
            migrationBuilder.Sql(@"
                ALTER TABLE project_variable_set_links ADD COLUMN space_id uuid;
                UPDATE project_variable_set_links l
                    SET space_id = p.space_id
                    FROM projects p
                    WHERE p.id = l.project_id;
                ALTER TABLE project_variable_set_links ALTER COLUMN space_id SET NOT NULL;");

            migrationBuilder.AddUniqueConstraint(
                name: "ak_variable_sets_space_id_id",
                table: "variable_sets",
                columns: new[] { "space_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_tenants_space_id_id",
                table: "tenants",
                columns: new[] { "space_id", "id" });

            migrationBuilder.AddPrimaryKey(
                name: "pk_target_tenants",
                table: "target_tenants",
                columns: new[] { "target_id", "tenant_id" });

            migrationBuilder.AddPrimaryKey(
                name: "pk_target_environments",
                table: "target_environments",
                columns: new[] { "target_id", "environment_id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_tags_space_id_id",
                table: "tags",
                columns: new[] { "space_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_tag_sets_space_id_id",
                table: "tag_sets",
                columns: new[] { "space_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_server_tasks_space_id_id",
                table: "server_tasks",
                columns: new[] { "space_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_runbooks_space_id_id",
                table: "runbooks",
                columns: new[] { "space_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_releases_space_id_id",
                table: "releases",
                columns: new[] { "space_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_projects_space_id_id",
                table: "projects",
                columns: new[] { "space_id", "id" });

            migrationBuilder.AddPrimaryKey(
                name: "pk_project_tenants",
                table: "project_tenants",
                columns: new[] { "project_id", "tenant_id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_project_groups_space_id_id",
                table: "project_groups",
                columns: new[] { "space_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_processes_space_id_id",
                table: "processes",
                columns: new[] { "space_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_process_steps_space_id_id",
                table: "process_steps",
                columns: new[] { "space_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_lifecycles_space_id_id",
                table: "lifecycles",
                columns: new[] { "space_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_environments_space_id_id",
                table: "environments",
                columns: new[] { "space_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_deployment_targets_space_id_id",
                table: "deployment_targets",
                columns: new[] { "space_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_channels_space_id_id",
                table: "channels",
                columns: new[] { "space_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_adhoc_sessions_space_id_id",
                table: "adhoc_sessions",
                columns: new[] { "space_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_variables_space_id_set_id",
                table: "variables",
                columns: new[] { "space_id", "set_id" });

            migrationBuilder.CreateIndex(
                name: "ix_variable_sets_space_id_project_id",
                table: "variable_sets",
                columns: new[] { "space_id", "project_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tenants_space_id_variable_set_id",
                table: "tenants",
                columns: new[] { "space_id", "variable_set_id" });

            migrationBuilder.CreateIndex(
                name: "ix_task_target_assignments_space_id_target_id",
                table: "task_target_assignments",
                columns: new[] { "space_id", "target_id" });

            migrationBuilder.CreateIndex(
                name: "ix_task_target_assignments_space_id_task_id",
                table: "task_target_assignments",
                columns: new[] { "space_id", "task_id" });

            migrationBuilder.CreateIndex(
                name: "ix_task_step_outcomes_space_id_target_id",
                table: "task_step_outcomes",
                columns: new[] { "space_id", "target_id" });

            migrationBuilder.CreateIndex(
                name: "ix_task_step_outcomes_space_id_task_id",
                table: "task_step_outcomes",
                columns: new[] { "space_id", "task_id" });

            migrationBuilder.CreateIndex(
                name: "ix_task_output_variables_space_id_task_id",
                table: "task_output_variables",
                columns: new[] { "space_id", "task_id" });

            migrationBuilder.CreateIndex(
                name: "ix_task_artifacts_space_id_task_id",
                table: "task_artifacts",
                columns: new[] { "space_id", "task_id" });

            migrationBuilder.CreateIndex(
                name: "ix_target_tenants_space_id_target_id",
                table: "target_tenants",
                columns: new[] { "space_id", "target_id" });

            migrationBuilder.CreateIndex(
                name: "ix_target_tenants_space_id_tenant_id",
                table: "target_tenants",
                columns: new[] { "space_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_target_tenants_tenant_id",
                table: "target_tenants",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_target_environments_environment_id",
                table: "target_environments",
                column: "environment_id");

            migrationBuilder.CreateIndex(
                name: "ix_target_environments_space_id_environment_id",
                table: "target_environments",
                columns: new[] { "space_id", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_target_environments_space_id_target_id",
                table: "target_environments",
                columns: new[] { "space_id", "target_id" });

            migrationBuilder.CreateIndex(
                name: "ix_tags_space_id_tag_set_id",
                table: "tags",
                columns: new[] { "space_id", "tag_set_id" });

            migrationBuilder.CreateIndex(
                name: "ix_tag_applications_space_id_tag_id",
                table: "tag_applications",
                columns: new[] { "space_id", "tag_id" });

            migrationBuilder.CreateIndex(
                name: "ix_tag_applications_space_id_tag_set_id",
                table: "tag_applications",
                columns: new[] { "space_id", "tag_set_id" });

            migrationBuilder.CreateIndex(
                name: "ix_server_tasks_space_id_environment_id",
                table: "server_tasks",
                columns: new[] { "space_id", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_server_tasks_space_id_parent_task_id",
                table: "server_tasks",
                columns: new[] { "space_id", "parent_task_id" });

            migrationBuilder.CreateIndex(
                name: "ix_server_tasks_space_id_release_id",
                table: "server_tasks",
                columns: new[] { "space_id", "release_id" });

            migrationBuilder.CreateIndex(
                name: "ix_server_tasks_space_id_runbook_id",
                table: "server_tasks",
                columns: new[] { "space_id", "runbook_id" });

            migrationBuilder.CreateIndex(
                name: "ix_server_tasks_space_id_tenant_id",
                table: "server_tasks",
                columns: new[] { "space_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_runbooks_space_id_project_id",
                table: "runbooks",
                columns: new[] { "space_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "ix_releases_space_id_channel_id",
                table: "releases",
                columns: new[] { "space_id", "channel_id" });

            migrationBuilder.CreateIndex(
                name: "ix_releases_space_id_project_id",
                table: "releases",
                columns: new[] { "space_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "ix_projects_space_id_lifecycle_id",
                table: "projects",
                columns: new[] { "space_id", "lifecycle_id" });

            migrationBuilder.CreateIndex(
                name: "ix_projects_space_id_project_group_id",
                table: "projects",
                columns: new[] { "space_id", "project_group_id" });

            migrationBuilder.CreateIndex(
                name: "ix_project_variable_set_links_space_id_project_id",
                table: "project_variable_set_links",
                columns: new[] { "space_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "ix_project_variable_set_links_space_id_variable_set_id",
                table: "project_variable_set_links",
                columns: new[] { "space_id", "variable_set_id" });

            migrationBuilder.CreateIndex(
                name: "ix_project_tenants_space_id_project_id",
                table: "project_tenants",
                columns: new[] { "space_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "ix_project_tenants_space_id_tenant_id",
                table: "project_tenants",
                columns: new[] { "space_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_project_tenants_tenant_id",
                table: "project_tenants",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_process_steps_space_id_parent_step_id",
                table: "process_steps",
                columns: new[] { "space_id", "parent_step_id" });

            migrationBuilder.CreateIndex(
                name: "ix_process_steps_space_id_process_id",
                table: "process_steps",
                columns: new[] { "space_id", "process_id" });

            migrationBuilder.CreateIndex(
                name: "ix_deployment_diagnoses_space_id_deployment_id",
                table: "deployment_diagnoses",
                columns: new[] { "space_id", "deployment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_channels_space_id_lifecycle_id",
                table: "channels",
                columns: new[] { "space_id", "lifecycle_id" });

            migrationBuilder.CreateIndex(
                name: "ix_channels_space_id_project_id",
                table: "channels",
                columns: new[] { "space_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "ix_adhoc_iterations_space_id_session_id",
                table: "adhoc_iterations",
                columns: new[] { "space_id", "session_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_adhoc_iterations_adhoc_sessions_space_id_session_id",
                table: "adhoc_iterations",
                columns: new[] { "space_id", "session_id" },
                principalTable: "adhoc_sessions",
                principalColumns: new[] { "space_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_channels_lifecycles_space_id_lifecycle_id",
                table: "channels",
                columns: new[] { "space_id", "lifecycle_id" },
                principalTable: "lifecycles",
                principalColumns: new[] { "space_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_channels_projects_space_id_project_id",
                table: "channels",
                columns: new[] { "space_id", "project_id" },
                principalTable: "projects",
                principalColumns: new[] { "space_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_deployment_diagnoses_server_tasks_space_id_deployment_id",
                table: "deployment_diagnoses",
                columns: new[] { "space_id", "deployment_id" },
                principalTable: "server_tasks",
                principalColumns: new[] { "space_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_process_steps_process_steps_space_id_parent_step_id",
                table: "process_steps",
                columns: new[] { "space_id", "parent_step_id" },
                principalTable: "process_steps",
                principalColumns: new[] { "space_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_process_steps_processes_space_id_process_id",
                table: "process_steps",
                columns: new[] { "space_id", "process_id" },
                principalTable: "processes",
                principalColumns: new[] { "space_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_project_tenants_projects_space_id_project_id",
                table: "project_tenants",
                columns: new[] { "space_id", "project_id" },
                principalTable: "projects",
                principalColumns: new[] { "space_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_project_tenants_tenants_space_id_tenant_id",
                table: "project_tenants",
                columns: new[] { "space_id", "tenant_id" },
                principalTable: "tenants",
                principalColumns: new[] { "space_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_project_variable_set_links_projects_space_id_project_id",
                table: "project_variable_set_links",
                columns: new[] { "space_id", "project_id" },
                principalTable: "projects",
                principalColumns: new[] { "space_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_project_variable_set_links_variable_sets_space_id_variable_",
                table: "project_variable_set_links",
                columns: new[] { "space_id", "variable_set_id" },
                principalTable: "variable_sets",
                principalColumns: new[] { "space_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_projects_lifecycles_space_id_lifecycle_id",
                table: "projects",
                columns: new[] { "space_id", "lifecycle_id" },
                principalTable: "lifecycles",
                principalColumns: new[] { "space_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_projects_project_groups_space_id_project_group_id",
                table: "projects",
                columns: new[] { "space_id", "project_group_id" },
                principalTable: "project_groups",
                principalColumns: new[] { "space_id", "id" },
                onDelete: ReferentialAction.Restrict);

            // Composite SET NULL FK — Postgres 15+ column-list form so ONLY
            // channel_id is nulled on delete (space_id is NOT NULL; a plain
            // `SET NULL` would try to null space_id and fail at delete time). EF
            // Core 10 cannot emit the column subset, so this is raw SQL. The model
            // still records it as SetNull (the subset is invisible to the model
            // comparison → no snapshot drift). Kept in sync by name on Down.
            migrationBuilder.Sql(@"
                ALTER TABLE releases
                    ADD CONSTRAINT fk_releases_channels_space_id_channel_id
                    FOREIGN KEY (space_id, channel_id)
                    REFERENCES channels (space_id, id)
                    ON DELETE SET NULL (channel_id);");

            migrationBuilder.AddForeignKey(
                name: "fk_releases_projects_space_id_project_id",
                table: "releases",
                columns: new[] { "space_id", "project_id" },
                principalTable: "projects",
                principalColumns: new[] { "space_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_runbooks_projects_space_id_project_id",
                table: "runbooks",
                columns: new[] { "space_id", "project_id" },
                principalTable: "projects",
                principalColumns: new[] { "space_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_server_tasks_environments_space_id_environment_id",
                table: "server_tasks",
                columns: new[] { "space_id", "environment_id" },
                principalTable: "environments",
                principalColumns: new[] { "space_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_server_tasks_releases_space_id_release_id",
                table: "server_tasks",
                columns: new[] { "space_id", "release_id" },
                principalTable: "releases",
                principalColumns: new[] { "space_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_server_tasks_runbooks_space_id_runbook_id",
                table: "server_tasks",
                columns: new[] { "space_id", "runbook_id" },
                principalTable: "runbooks",
                principalColumns: new[] { "space_id", "id" },
                onDelete: ReferentialAction.Restrict);

            // Composite self SET NULL FK — column-list form (nulls only
            // parent_task_id, keeps NOT NULL space_id). Raw SQL, see channel_id above.
            migrationBuilder.Sql(@"
                ALTER TABLE server_tasks
                    ADD CONSTRAINT fk_server_tasks_server_tasks_space_id_parent_task_id
                    FOREIGN KEY (space_id, parent_task_id)
                    REFERENCES server_tasks (space_id, id)
                    ON DELETE SET NULL (parent_task_id);");

            // Composite SET NULL FK — column-list form (nulls only tenant_id). Raw SQL.
            migrationBuilder.Sql(@"
                ALTER TABLE server_tasks
                    ADD CONSTRAINT fk_server_tasks_tenants_space_id_tenant_id
                    FOREIGN KEY (space_id, tenant_id)
                    REFERENCES tenants (space_id, id)
                    ON DELETE SET NULL (tenant_id);");

            migrationBuilder.AddForeignKey(
                name: "fk_tag_applications_tag_sets_space_id_tag_set_id",
                table: "tag_applications",
                columns: new[] { "space_id", "tag_set_id" },
                principalTable: "tag_sets",
                principalColumns: new[] { "space_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_tag_applications_tags_space_id_tag_id",
                table: "tag_applications",
                columns: new[] { "space_id", "tag_id" },
                principalTable: "tags",
                principalColumns: new[] { "space_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_tags_tag_sets_space_id_tag_set_id",
                table: "tags",
                columns: new[] { "space_id", "tag_set_id" },
                principalTable: "tag_sets",
                principalColumns: new[] { "space_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_target_environments_deployment_targets_space_id_target_id",
                table: "target_environments",
                columns: new[] { "space_id", "target_id" },
                principalTable: "deployment_targets",
                principalColumns: new[] { "space_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_target_environments_environments_space_id_environment_id",
                table: "target_environments",
                columns: new[] { "space_id", "environment_id" },
                principalTable: "environments",
                principalColumns: new[] { "space_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_target_tenants_deployment_targets_space_id_target_id",
                table: "target_tenants",
                columns: new[] { "space_id", "target_id" },
                principalTable: "deployment_targets",
                principalColumns: new[] { "space_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_target_tenants_tenants_space_id_tenant_id",
                table: "target_tenants",
                columns: new[] { "space_id", "tenant_id" },
                principalTable: "tenants",
                principalColumns: new[] { "space_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_task_artifacts_server_tasks_space_id_task_id",
                table: "task_artifacts",
                columns: new[] { "space_id", "task_id" },
                principalTable: "server_tasks",
                principalColumns: new[] { "space_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_task_output_variables_server_tasks_space_id_task_id",
                table: "task_output_variables",
                columns: new[] { "space_id", "task_id" },
                principalTable: "server_tasks",
                principalColumns: new[] { "space_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_task_step_outcomes_deployment_targets_space_id_target_id",
                table: "task_step_outcomes",
                columns: new[] { "space_id", "target_id" },
                principalTable: "deployment_targets",
                principalColumns: new[] { "space_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_task_step_outcomes_server_tasks_space_id_task_id",
                table: "task_step_outcomes",
                columns: new[] { "space_id", "task_id" },
                principalTable: "server_tasks",
                principalColumns: new[] { "space_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_task_target_assignments_deployment_targets_space_id_target_",
                table: "task_target_assignments",
                columns: new[] { "space_id", "target_id" },
                principalTable: "deployment_targets",
                principalColumns: new[] { "space_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_task_target_assignments_server_tasks_space_id_task_id",
                table: "task_target_assignments",
                columns: new[] { "space_id", "task_id" },
                principalTable: "server_tasks",
                principalColumns: new[] { "space_id", "id" },
                onDelete: ReferentialAction.Cascade);

            // Composite SET NULL FK — column-list form (nulls only variable_set_id). Raw SQL.
            migrationBuilder.Sql(@"
                ALTER TABLE tenants
                    ADD CONSTRAINT fk_tenants_variable_sets_space_id_variable_set_id
                    FOREIGN KEY (space_id, variable_set_id)
                    REFERENCES variable_sets (space_id, id)
                    ON DELETE SET NULL (variable_set_id);");

            migrationBuilder.AddForeignKey(
                name: "fk_variable_sets_projects_space_id_project_id",
                table: "variable_sets",
                columns: new[] { "space_id", "project_id" },
                principalTable: "projects",
                principalColumns: new[] { "space_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_variables_variable_sets_space_id_set_id",
                table: "variables",
                columns: new[] { "space_id", "set_id" },
                principalTable: "variable_sets",
                principalColumns: new[] { "space_id", "id" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_adhoc_iterations_adhoc_sessions_space_id_session_id",
                table: "adhoc_iterations");

            migrationBuilder.DropForeignKey(
                name: "fk_channels_lifecycles_space_id_lifecycle_id",
                table: "channels");

            migrationBuilder.DropForeignKey(
                name: "fk_channels_projects_space_id_project_id",
                table: "channels");

            migrationBuilder.DropForeignKey(
                name: "fk_deployment_diagnoses_server_tasks_space_id_deployment_id",
                table: "deployment_diagnoses");

            migrationBuilder.DropForeignKey(
                name: "fk_process_steps_process_steps_space_id_parent_step_id",
                table: "process_steps");

            migrationBuilder.DropForeignKey(
                name: "fk_process_steps_processes_space_id_process_id",
                table: "process_steps");

            migrationBuilder.DropForeignKey(
                name: "fk_project_tenants_projects_space_id_project_id",
                table: "project_tenants");

            migrationBuilder.DropForeignKey(
                name: "fk_project_tenants_tenants_space_id_tenant_id",
                table: "project_tenants");

            migrationBuilder.DropForeignKey(
                name: "fk_project_variable_set_links_projects_space_id_project_id",
                table: "project_variable_set_links");

            migrationBuilder.DropForeignKey(
                name: "fk_project_variable_set_links_variable_sets_space_id_variable_",
                table: "project_variable_set_links");

            migrationBuilder.DropForeignKey(
                name: "fk_projects_lifecycles_space_id_lifecycle_id",
                table: "projects");

            migrationBuilder.DropForeignKey(
                name: "fk_projects_project_groups_space_id_project_group_id",
                table: "projects");

            migrationBuilder.DropForeignKey(
                name: "fk_releases_channels_space_id_channel_id",
                table: "releases");

            migrationBuilder.DropForeignKey(
                name: "fk_releases_projects_space_id_project_id",
                table: "releases");

            migrationBuilder.DropForeignKey(
                name: "fk_runbooks_projects_space_id_project_id",
                table: "runbooks");

            migrationBuilder.DropForeignKey(
                name: "fk_server_tasks_environments_space_id_environment_id",
                table: "server_tasks");

            migrationBuilder.DropForeignKey(
                name: "fk_server_tasks_releases_space_id_release_id",
                table: "server_tasks");

            migrationBuilder.DropForeignKey(
                name: "fk_server_tasks_runbooks_space_id_runbook_id",
                table: "server_tasks");

            migrationBuilder.DropForeignKey(
                name: "fk_server_tasks_server_tasks_space_id_parent_task_id",
                table: "server_tasks");

            migrationBuilder.DropForeignKey(
                name: "fk_server_tasks_tenants_space_id_tenant_id",
                table: "server_tasks");

            migrationBuilder.DropForeignKey(
                name: "fk_tag_applications_tag_sets_space_id_tag_set_id",
                table: "tag_applications");

            migrationBuilder.DropForeignKey(
                name: "fk_tag_applications_tags_space_id_tag_id",
                table: "tag_applications");

            migrationBuilder.DropForeignKey(
                name: "fk_tags_tag_sets_space_id_tag_set_id",
                table: "tags");

            migrationBuilder.DropForeignKey(
                name: "fk_target_environments_deployment_targets_space_id_target_id",
                table: "target_environments");

            migrationBuilder.DropForeignKey(
                name: "fk_target_environments_environments_space_id_environment_id",
                table: "target_environments");

            migrationBuilder.DropForeignKey(
                name: "fk_target_tenants_deployment_targets_space_id_target_id",
                table: "target_tenants");

            migrationBuilder.DropForeignKey(
                name: "fk_target_tenants_tenants_space_id_tenant_id",
                table: "target_tenants");

            migrationBuilder.DropForeignKey(
                name: "fk_task_artifacts_server_tasks_space_id_task_id",
                table: "task_artifacts");

            migrationBuilder.DropForeignKey(
                name: "fk_task_output_variables_server_tasks_space_id_task_id",
                table: "task_output_variables");

            migrationBuilder.DropForeignKey(
                name: "fk_task_step_outcomes_deployment_targets_space_id_target_id",
                table: "task_step_outcomes");

            migrationBuilder.DropForeignKey(
                name: "fk_task_step_outcomes_server_tasks_space_id_task_id",
                table: "task_step_outcomes");

            migrationBuilder.DropForeignKey(
                name: "fk_task_target_assignments_deployment_targets_space_id_target_",
                table: "task_target_assignments");

            migrationBuilder.DropForeignKey(
                name: "fk_task_target_assignments_server_tasks_space_id_task_id",
                table: "task_target_assignments");

            migrationBuilder.DropForeignKey(
                name: "fk_tenants_variable_sets_space_id_variable_set_id",
                table: "tenants");

            migrationBuilder.DropForeignKey(
                name: "fk_variable_sets_projects_space_id_project_id",
                table: "variable_sets");

            migrationBuilder.DropForeignKey(
                name: "fk_variables_variable_sets_space_id_set_id",
                table: "variables");

            migrationBuilder.DropIndex(
                name: "ix_variables_space_id_set_id",
                table: "variables");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_variable_sets_space_id_id",
                table: "variable_sets");

            migrationBuilder.DropIndex(
                name: "ix_variable_sets_space_id_project_id",
                table: "variable_sets");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_tenants_space_id_id",
                table: "tenants");

            migrationBuilder.DropIndex(
                name: "ix_tenants_space_id_variable_set_id",
                table: "tenants");

            migrationBuilder.DropIndex(
                name: "ix_task_target_assignments_space_id_target_id",
                table: "task_target_assignments");

            migrationBuilder.DropIndex(
                name: "ix_task_target_assignments_space_id_task_id",
                table: "task_target_assignments");

            migrationBuilder.DropIndex(
                name: "ix_task_step_outcomes_space_id_target_id",
                table: "task_step_outcomes");

            migrationBuilder.DropIndex(
                name: "ix_task_step_outcomes_space_id_task_id",
                table: "task_step_outcomes");

            migrationBuilder.DropIndex(
                name: "ix_task_output_variables_space_id_task_id",
                table: "task_output_variables");

            migrationBuilder.DropIndex(
                name: "ix_task_artifacts_space_id_task_id",
                table: "task_artifacts");

            migrationBuilder.DropPrimaryKey(
                name: "pk_target_tenants",
                table: "target_tenants");

            migrationBuilder.DropIndex(
                name: "ix_target_tenants_space_id_target_id",
                table: "target_tenants");

            migrationBuilder.DropIndex(
                name: "ix_target_tenants_space_id_tenant_id",
                table: "target_tenants");

            migrationBuilder.DropIndex(
                name: "ix_target_tenants_tenant_id",
                table: "target_tenants");

            migrationBuilder.DropPrimaryKey(
                name: "pk_target_environments",
                table: "target_environments");

            migrationBuilder.DropIndex(
                name: "ix_target_environments_environment_id",
                table: "target_environments");

            migrationBuilder.DropIndex(
                name: "ix_target_environments_space_id_environment_id",
                table: "target_environments");

            migrationBuilder.DropIndex(
                name: "ix_target_environments_space_id_target_id",
                table: "target_environments");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_tags_space_id_id",
                table: "tags");

            migrationBuilder.DropIndex(
                name: "ix_tags_space_id_tag_set_id",
                table: "tags");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_tag_sets_space_id_id",
                table: "tag_sets");

            migrationBuilder.DropIndex(
                name: "ix_tag_applications_space_id_tag_id",
                table: "tag_applications");

            migrationBuilder.DropIndex(
                name: "ix_tag_applications_space_id_tag_set_id",
                table: "tag_applications");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_server_tasks_space_id_id",
                table: "server_tasks");

            migrationBuilder.DropIndex(
                name: "ix_server_tasks_space_id_environment_id",
                table: "server_tasks");

            migrationBuilder.DropIndex(
                name: "ix_server_tasks_space_id_parent_task_id",
                table: "server_tasks");

            migrationBuilder.DropIndex(
                name: "ix_server_tasks_space_id_release_id",
                table: "server_tasks");

            migrationBuilder.DropIndex(
                name: "ix_server_tasks_space_id_runbook_id",
                table: "server_tasks");

            migrationBuilder.DropIndex(
                name: "ix_server_tasks_space_id_tenant_id",
                table: "server_tasks");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_runbooks_space_id_id",
                table: "runbooks");

            migrationBuilder.DropIndex(
                name: "ix_runbooks_space_id_project_id",
                table: "runbooks");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_releases_space_id_id",
                table: "releases");

            migrationBuilder.DropIndex(
                name: "ix_releases_space_id_channel_id",
                table: "releases");

            migrationBuilder.DropIndex(
                name: "ix_releases_space_id_project_id",
                table: "releases");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_projects_space_id_id",
                table: "projects");

            migrationBuilder.DropIndex(
                name: "ix_projects_space_id_lifecycle_id",
                table: "projects");

            migrationBuilder.DropIndex(
                name: "ix_projects_space_id_project_group_id",
                table: "projects");

            migrationBuilder.DropIndex(
                name: "ix_project_variable_set_links_space_id_project_id",
                table: "project_variable_set_links");

            migrationBuilder.DropIndex(
                name: "ix_project_variable_set_links_space_id_variable_set_id",
                table: "project_variable_set_links");

            migrationBuilder.DropPrimaryKey(
                name: "pk_project_tenants",
                table: "project_tenants");

            migrationBuilder.DropIndex(
                name: "ix_project_tenants_space_id_project_id",
                table: "project_tenants");

            migrationBuilder.DropIndex(
                name: "ix_project_tenants_space_id_tenant_id",
                table: "project_tenants");

            migrationBuilder.DropIndex(
                name: "ix_project_tenants_tenant_id",
                table: "project_tenants");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_project_groups_space_id_id",
                table: "project_groups");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_processes_space_id_id",
                table: "processes");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_process_steps_space_id_id",
                table: "process_steps");

            migrationBuilder.DropIndex(
                name: "ix_process_steps_space_id_parent_step_id",
                table: "process_steps");

            migrationBuilder.DropIndex(
                name: "ix_process_steps_space_id_process_id",
                table: "process_steps");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_lifecycles_space_id_id",
                table: "lifecycles");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_environments_space_id_id",
                table: "environments");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_deployment_targets_space_id_id",
                table: "deployment_targets");

            migrationBuilder.DropIndex(
                name: "ix_deployment_diagnoses_space_id_deployment_id",
                table: "deployment_diagnoses");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_channels_space_id_id",
                table: "channels");

            migrationBuilder.DropIndex(
                name: "ix_channels_space_id_lifecycle_id",
                table: "channels");

            migrationBuilder.DropIndex(
                name: "ix_channels_space_id_project_id",
                table: "channels");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_adhoc_sessions_space_id_id",
                table: "adhoc_sessions");

            migrationBuilder.DropIndex(
                name: "ix_adhoc_iterations_space_id_session_id",
                table: "adhoc_iterations");

            migrationBuilder.DropColumn(
                name: "space_id",
                table: "task_target_assignments");

            migrationBuilder.DropColumn(
                name: "target_id",
                table: "target_tenants");

            migrationBuilder.DropColumn(
                name: "target_id",
                table: "target_environments");

            migrationBuilder.DropColumn(
                name: "space_id",
                table: "project_variable_set_links");

            migrationBuilder.DropColumn(
                name: "project_id",
                table: "project_tenants");

            migrationBuilder.RenameColumn(
                name: "space_id",
                table: "target_tenants",
                newName: "tenants_id");

            migrationBuilder.RenameColumn(
                name: "tenant_id",
                table: "target_tenants",
                newName: "deployment_target_id");

            migrationBuilder.RenameColumn(
                name: "space_id",
                table: "target_environments",
                newName: "environments_id");

            migrationBuilder.RenameColumn(
                name: "environment_id",
                table: "target_environments",
                newName: "deployment_target_id");

            migrationBuilder.RenameColumn(
                name: "space_id",
                table: "project_tenants",
                newName: "tenants_id");

            migrationBuilder.RenameColumn(
                name: "tenant_id",
                table: "project_tenants",
                newName: "projects_id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_target_tenants",
                table: "target_tenants",
                columns: new[] { "deployment_target_id", "tenants_id" });

            migrationBuilder.AddPrimaryKey(
                name: "pk_target_environments",
                table: "target_environments",
                columns: new[] { "deployment_target_id", "environments_id" });

            migrationBuilder.AddPrimaryKey(
                name: "pk_project_tenants",
                table: "project_tenants",
                columns: new[] { "projects_id", "tenants_id" });

            migrationBuilder.CreateIndex(
                name: "ix_variables_space_id",
                table: "variables",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_task_step_outcomes_space_id",
                table: "task_step_outcomes",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_task_step_outcomes_target_id",
                table: "task_step_outcomes",
                column: "target_id");

            migrationBuilder.CreateIndex(
                name: "ix_task_output_variables_space_id",
                table: "task_output_variables",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_task_artifacts_space_id",
                table: "task_artifacts",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_target_tenants_tenants_id",
                table: "target_tenants",
                column: "tenants_id");

            migrationBuilder.CreateIndex(
                name: "ix_target_environments_environments_id",
                table: "target_environments",
                column: "environments_id");

            migrationBuilder.CreateIndex(
                name: "ix_tags_space_id",
                table: "tags",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_tag_applications_space_id",
                table: "tag_applications",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_server_tasks_environment_id",
                table: "server_tasks",
                column: "environment_id");

            migrationBuilder.CreateIndex(
                name: "ix_server_tasks_tenant_id",
                table: "server_tasks",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_runbooks_space_id",
                table: "runbooks",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_releases_channel_id",
                table: "releases",
                column: "channel_id");

            migrationBuilder.CreateIndex(
                name: "ix_releases_space_id",
                table: "releases",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_projects_lifecycle_id",
                table: "projects",
                column: "lifecycle_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_tenants_tenants_id",
                table: "project_tenants",
                column: "tenants_id");

            migrationBuilder.CreateIndex(
                name: "ix_process_steps_space_id",
                table: "process_steps",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployment_diagnoses_space_id",
                table: "deployment_diagnoses",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_channels_lifecycle_id",
                table: "channels",
                column: "lifecycle_id");

            migrationBuilder.CreateIndex(
                name: "ix_channels_space_id",
                table: "channels",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_adhoc_iterations_space_id",
                table: "adhoc_iterations",
                column: "space_id");

            migrationBuilder.AddForeignKey(
                name: "fk_adhoc_iterations_adhoc_sessions_session_id",
                table: "adhoc_iterations",
                column: "session_id",
                principalTable: "adhoc_sessions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_adhoc_iterations_spaces_space_id",
                table: "adhoc_iterations",
                column: "space_id",
                principalTable: "spaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_channels_lifecycles_lifecycle_id",
                table: "channels",
                column: "lifecycle_id",
                principalTable: "lifecycles",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_channels_projects_project_id",
                table: "channels",
                column: "project_id",
                principalTable: "projects",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_channels_spaces_space_id",
                table: "channels",
                column: "space_id",
                principalTable: "spaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_deployment_diagnoses_server_tasks_deployment_id",
                table: "deployment_diagnoses",
                column: "deployment_id",
                principalTable: "server_tasks",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_deployment_diagnoses_spaces_space_id",
                table: "deployment_diagnoses",
                column: "space_id",
                principalTable: "spaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_process_steps_process_steps_parent_step_id",
                table: "process_steps",
                column: "parent_step_id",
                principalTable: "process_steps",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_process_steps_processes_process_id",
                table: "process_steps",
                column: "process_id",
                principalTable: "processes",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_process_steps_spaces_space_id",
                table: "process_steps",
                column: "space_id",
                principalTable: "spaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_project_tenants_projects_projects_id",
                table: "project_tenants",
                column: "projects_id",
                principalTable: "projects",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_project_tenants_tenants_tenants_id",
                table: "project_tenants",
                column: "tenants_id",
                principalTable: "tenants",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_project_variable_set_links_projects_project_id",
                table: "project_variable_set_links",
                column: "project_id",
                principalTable: "projects",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_project_variable_set_links_variable_sets_variable_set_id",
                table: "project_variable_set_links",
                column: "variable_set_id",
                principalTable: "variable_sets",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_projects_lifecycles_lifecycle_id",
                table: "projects",
                column: "lifecycle_id",
                principalTable: "lifecycles",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_projects_project_groups_project_group_id",
                table: "projects",
                column: "project_group_id",
                principalTable: "project_groups",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_releases_channels_channel_id",
                table: "releases",
                column: "channel_id",
                principalTable: "channels",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_releases_projects_project_id",
                table: "releases",
                column: "project_id",
                principalTable: "projects",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_releases_spaces_space_id",
                table: "releases",
                column: "space_id",
                principalTable: "spaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_runbooks_projects_project_id",
                table: "runbooks",
                column: "project_id",
                principalTable: "projects",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_runbooks_spaces_space_id",
                table: "runbooks",
                column: "space_id",
                principalTable: "spaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_server_tasks_environments_environment_id",
                table: "server_tasks",
                column: "environment_id",
                principalTable: "environments",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_server_tasks_releases_release_id",
                table: "server_tasks",
                column: "release_id",
                principalTable: "releases",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_server_tasks_runbooks_runbook_id",
                table: "server_tasks",
                column: "runbook_id",
                principalTable: "runbooks",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_server_tasks_server_tasks_parent_task_id",
                table: "server_tasks",
                column: "parent_task_id",
                principalTable: "server_tasks",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_server_tasks_tenants_tenant_id",
                table: "server_tasks",
                column: "tenant_id",
                principalTable: "tenants",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_tag_applications_spaces_space_id",
                table: "tag_applications",
                column: "space_id",
                principalTable: "spaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_tag_applications_tag_sets_tag_set_id",
                table: "tag_applications",
                column: "tag_set_id",
                principalTable: "tag_sets",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_tag_applications_tags_tag_id",
                table: "tag_applications",
                column: "tag_id",
                principalTable: "tags",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_tags_spaces_space_id",
                table: "tags",
                column: "space_id",
                principalTable: "spaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_tags_tag_sets_tag_set_id",
                table: "tags",
                column: "tag_set_id",
                principalTable: "tag_sets",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_target_environments_deployment_targets_deployment_target_id",
                table: "target_environments",
                column: "deployment_target_id",
                principalTable: "deployment_targets",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_target_environments_environments_environments_id",
                table: "target_environments",
                column: "environments_id",
                principalTable: "environments",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_target_tenants_deployment_targets_deployment_target_id",
                table: "target_tenants",
                column: "deployment_target_id",
                principalTable: "deployment_targets",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_target_tenants_tenants_tenants_id",
                table: "target_tenants",
                column: "tenants_id",
                principalTable: "tenants",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_task_artifacts_server_tasks_task_id",
                table: "task_artifacts",
                column: "task_id",
                principalTable: "server_tasks",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_task_artifacts_spaces_space_id",
                table: "task_artifacts",
                column: "space_id",
                principalTable: "spaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_task_output_variables_server_tasks_task_id",
                table: "task_output_variables",
                column: "task_id",
                principalTable: "server_tasks",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_task_output_variables_spaces_space_id",
                table: "task_output_variables",
                column: "space_id",
                principalTable: "spaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_task_step_outcomes_deployment_targets_target_id",
                table: "task_step_outcomes",
                column: "target_id",
                principalTable: "deployment_targets",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_task_step_outcomes_server_tasks_task_id",
                table: "task_step_outcomes",
                column: "task_id",
                principalTable: "server_tasks",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_task_step_outcomes_spaces_space_id",
                table: "task_step_outcomes",
                column: "space_id",
                principalTable: "spaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_task_target_assignments_deployment_targets_target_id",
                table: "task_target_assignments",
                column: "target_id",
                principalTable: "deployment_targets",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_task_target_assignments_server_tasks_task_id",
                table: "task_target_assignments",
                column: "task_id",
                principalTable: "server_tasks",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_tenants_variable_sets_variable_set_id",
                table: "tenants",
                column: "variable_set_id",
                principalTable: "variable_sets",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_variable_sets_projects_project_id",
                table: "variable_sets",
                column: "project_id",
                principalTable: "projects",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_variables_spaces_space_id",
                table: "variables",
                column: "space_id",
                principalTable: "spaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_variables_variable_sets_set_id",
                table: "variables",
                column: "set_id",
                principalTable: "variable_sets",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
