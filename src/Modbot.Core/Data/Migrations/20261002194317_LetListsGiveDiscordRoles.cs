using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class LetListsGiveDiscordRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "discord_list_roles_on",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "list_roles_problem",
                table: "discord_sync_state",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "list_roles_ran_at",
                table: "discord_sync_state",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "discord_list_role",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    discord_role_id = table.Column<string>(type: "text", nullable: false),
                    discord_role_name = table.Column<string>(type: "text", nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    problem = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    stopped_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    stopped_taking = table.Column<int>(type: "integer", nullable: true),
                    removals_allowed = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discord_list_role", x => x.id);
                    table.ForeignKey(
                        name: "fk_discord_list_role_saved_lists_list_id",
                        column: x => x.list_id,
                        principalTable: "saved_list",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "discord_list_role_given",
                columns: table => new
                {
                    list_role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    discord_user_id = table.Column<string>(type: "text", nullable: false),
                    vrchat_user_id = table.Column<string>(type: "text", nullable: true),
                    given_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discord_list_role_given", x => new { x.list_role_id, x.discord_user_id });
                    table.ForeignKey(
                        name: "fk_discord_list_role_given_discord_list_role_list_role_id",
                        column: x => x.list_role_id,
                        principalTable: "discord_list_role",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_discord_list_role_list",
                table: "discord_list_role",
                column: "list_id");

            migrationBuilder.CreateIndex(
                name: "ux_discord_list_role_role",
                table: "discord_list_role",
                column: "discord_role_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_discord_list_role_given_discord",
                table: "discord_list_role_given",
                column: "discord_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_discord_list_role_given_vrchat",
                table: "discord_list_role_given",
                column: "vrchat_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "discord_list_role_given");

            migrationBuilder.DropTable(
                name: "discord_list_role");

            migrationBuilder.DropColumn(
                name: "discord_list_roles_on",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "list_roles_problem",
                table: "discord_sync_state");

            migrationBuilder.DropColumn(
                name: "list_roles_ran_at",
                table: "discord_sync_state");
        }
    }
}
