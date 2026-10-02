using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class LetDiscordRolesGiveModbotRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "discord_staff_roles_on",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "from_discord",
                table: "modbot_user_role",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "staff_roles_held_at",
                table: "discord_sync_state",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "staff_roles_held_count",
                table: "discord_sync_state",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "staff_roles_problem",
                table: "discord_sync_state",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "staff_roles_ran_at",
                table: "discord_sync_state",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "discord_staff_role",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    discord_role_id = table.Column<string>(type: "text", nullable: false),
                    discord_role_name = table.Column<string>(type: "text", nullable: true),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    direction = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    problem = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    refused_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discord_staff_role", x => x.id);
                    table.ForeignKey(
                        name: "fk_discord_staff_role_modbot_role_role_id",
                        column: x => x.role_id,
                        principalTable: "modbot_role",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "discord_staff_role_state",
                columns: table => new
                {
                    mapping_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    discord_user_id = table.Column<string>(type: "text", nullable: false),
                    held = table.Column<bool>(type: "boolean", nullable: false),
                    agreed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discord_staff_role_state", x => new { x.mapping_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_discord_staff_role_state_discord_staff_role_mapping_id",
                        column: x => x.mapping_id,
                        principalTable: "discord_staff_role",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_discord_staff_role_state_modbot_user_user_id",
                        column: x => x.user_id,
                        principalTable: "modbot_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_discord_staff_role_role",
                table: "discord_staff_role",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ux_discord_staff_role_both_ways",
                table: "discord_staff_role",
                column: "role_id",
                unique: true,
                filter: "direction = 'both'");

            migrationBuilder.CreateIndex(
                name: "ux_discord_staff_role_discord",
                table: "discord_staff_role",
                column: "discord_role_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_discord_staff_role_state_user_id",
                table: "discord_staff_role_state",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "discord_staff_role_state");

            migrationBuilder.DropTable(
                name: "discord_staff_role");

            migrationBuilder.DropColumn(
                name: "discord_staff_roles_on",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "from_discord",
                table: "modbot_user_role");

            migrationBuilder.DropColumn(
                name: "staff_roles_held_at",
                table: "discord_sync_state");

            migrationBuilder.DropColumn(
                name: "staff_roles_held_count",
                table: "discord_sync_state");

            migrationBuilder.DropColumn(
                name: "staff_roles_problem",
                table: "discord_sync_state");

            migrationBuilder.DropColumn(
                name: "staff_roles_ran_at",
                table: "discord_sync_state");
        }
    }
}
