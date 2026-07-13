using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantVariableSetAndOidcFks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_users_last_oidc_provider_id",
                table: "users",
                column: "last_oidc_provider_id");

            migrationBuilder.CreateIndex(
                name: "ix_tenants_variable_set_id",
                table: "tenants",
                column: "variable_set_id");

            // Defensive: null any dangling pointers before enforcing SET NULL
            // FKs (a last_oidc_provider_id whose IdP was deleted would fail FK
            // creation). variable_set_id is dormant/all-null but swept anyway.
            migrationBuilder.Sql(
                "UPDATE users SET last_oidc_provider_id = NULL WHERE last_oidc_provider_id IS NOT NULL " +
                "AND last_oidc_provider_id NOT IN (SELECT id FROM identity_providers);");
            migrationBuilder.Sql(
                "UPDATE tenants SET variable_set_id = NULL WHERE variable_set_id IS NOT NULL " +
                "AND variable_set_id NOT IN (SELECT id FROM variable_sets);");

            migrationBuilder.AddForeignKey(
                name: "fk_tenants_variable_sets_variable_set_id",
                table: "tenants",
                column: "variable_set_id",
                principalTable: "variable_sets",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_users_identity_providers_last_oidc_provider_id",
                table: "users",
                column: "last_oidc_provider_id",
                principalTable: "identity_providers",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_tenants_variable_sets_variable_set_id",
                table: "tenants");

            migrationBuilder.DropForeignKey(
                name: "fk_users_identity_providers_last_oidc_provider_id",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_users_last_oidc_provider_id",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_tenants_variable_set_id",
                table: "tenants");
        }
    }
}
