using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscordRoleAndBanSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "discord_ban_copy_action",
                table: "settings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "discord_ban_sync_to_discord",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "discord_ban_sync_to_vr_chat",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "discord_role_sync_on",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "bot_can_ban_members",
                table: "discord_server",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "bot_can_remove_members",
                table: "discord_server",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "discord_copied_action",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    direction = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    subject_id = table.Column<string>(type: "text", nullable: false),
                    other_side_id = table.Column<string>(type: "text", nullable: true),
                    role_id = table.Column<string>(type: "text", nullable: true),
                    caused_by_fact_id = table.Column<long>(type: "bigint", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    done = table.Column<bool>(type: "boolean", nullable: true),
                    error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    seen_back_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discord_copied_action", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "discord_role_pair",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    vr_chat_role_id = table.Column<string>(type: "text", nullable: false),
                    discord_role_id = table.Column<string>(type: "text", nullable: false),
                    vr_chat_role_name = table.Column<string>(type: "text", nullable: true),
                    discord_role_name = table.Column<string>(type: "text", nullable: true),
                    decides = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    problem = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discord_role_pair", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "discord_sync_state",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    bans_read_through = table.Column<long>(type: "bigint", nullable: false),
                    bans_read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    bans_problem = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    roles_ran_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    roles_problem = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discord_sync_state", x => x.id);
                    table.CheckConstraint("ck_discord_sync_state_singleton", "id = 1");
                });

            migrationBuilder.CreateIndex(
                name: "ix_discord_copied_action_started",
                table: "discord_copied_action",
                column: "started_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_discord_copied_action_waiting",
                table: "discord_copied_action",
                columns: new[] { "direction", "subject_id", "kind", "started_at" },
                filter: "seen_back_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_discord_role_pair_discord",
                table: "discord_role_pair",
                column: "discord_role_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_discord_role_pair_vrchat",
                table: "discord_role_pair",
                column: "vr_chat_role_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "discord_copied_action");

            migrationBuilder.DropTable(
                name: "discord_role_pair");

            migrationBuilder.DropTable(
                name: "discord_sync_state");

            migrationBuilder.DropColumn(
                name: "discord_ban_copy_action",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "discord_ban_sync_to_discord",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "discord_ban_sync_to_vr_chat",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "discord_role_sync_on",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "bot_can_ban_members",
                table: "discord_server");

            migrationBuilder.DropColumn(
                name: "bot_can_remove_members",
                table: "discord_server");
        }
    }
}
