using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddM2Schema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "process_snapshot",
                table: "releases",
                type: "jsonb",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "release_notes",
                table: "releases",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "next_log_sequence",
                table: "deployments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "deployment_log_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    level = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployment_log_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_deployment_log_entries_deployments_deployment_id",
                        column: x => x.deployment_id,
                        principalTable: "deployments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "deployment_processes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployment_processes", x => x.id);
                    table.ForeignKey(
                        name: "fk_deployment_processes_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "packages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    package_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    file_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    stored_path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    uploaded_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_packages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "deployment_steps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    process_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    step_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    target_roles = table.Column<List<string>>(type: "text[]", nullable: false),
                    package_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    config = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployment_steps", x => x.id);
                    table.ForeignKey(
                        name: "fk_deployment_steps_deployment_processes_process_id",
                        column: x => x.process_id,
                        principalTable: "deployment_processes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_deployment_log_entries_deployment_id_sequence",
                table: "deployment_log_entries",
                columns: new[] { "deployment_id", "sequence" });

            migrationBuilder.CreateIndex(
                name: "ix_deployment_processes_project_id",
                table: "deployment_processes",
                column: "project_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_deployment_steps_process_id_sort_order",
                table: "deployment_steps",
                columns: new[] { "process_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ix_packages_package_id",
                table: "packages",
                column: "package_id");

            migrationBuilder.CreateIndex(
                name: "ix_packages_package_id_version",
                table: "packages",
                columns: new[] { "package_id", "version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deployment_log_entries");

            migrationBuilder.DropTable(
                name: "deployment_steps");

            migrationBuilder.DropTable(
                name: "packages");

            migrationBuilder.DropTable(
                name: "deployment_processes");

            migrationBuilder.DropColumn(
                name: "process_snapshot",
                table: "releases");

            migrationBuilder.DropColumn(
                name: "release_notes",
                table: "releases");

            migrationBuilder.DropColumn(
                name: "next_log_sequence",
                table: "deployments");
        }
    }
}
