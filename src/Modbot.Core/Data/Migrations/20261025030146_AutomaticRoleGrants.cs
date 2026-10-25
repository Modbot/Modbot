using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AutomaticRoleGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "automatic_role_grant",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    modbot_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    role_id = table.Column<string>(type: "text", nullable: false),
                    given_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    taken_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    take_reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_automatic_role_grant", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_automatic_role_grant_taken",
                table: "automatic_role_grant",
                columns: new[] { "modbot_user_id", "kind", "role_id", "taken_at" });

            migrationBuilder.CreateIndex(
                name: "ux_automatic_role_grant_open",
                table: "automatic_role_grant",
                columns: new[] { "modbot_user_id", "kind", "role_id" },
                unique: true,
                filter: "taken_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "automatic_role_grant");
        }
    }
}
