using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscordAccountLinking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "discord_eighteen_plus_role_id",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "discord_link_backup_channel_id",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "discord_link_prompt_new_members",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "discord_linked_role_id",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "discord_oauth_client_id",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "discord_oauth_client_secret_encrypted",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "discord_account_link",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    discord_user_id = table.Column<string>(type: "text", nullable: false),
                    discord_username = table.Column<string>(type: "text", nullable: false),
                    vrchat_user_id = table.Column<string>(type: "text", nullable: false),
                    vrchat_display_name = table.Column<string>(type: "text", nullable: true),
                    started_from = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    linked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    unlinked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    unlinked_by = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    unlinked_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    linked_role_id = table.Column<string>(type: "text", nullable: true),
                    eighteen_plus_role_id = table.Column<string>(type: "text", nullable: true),
                    not_in_server_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    role_error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discord_account_link", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "discord_link_code",
                columns: table => new
                {
                    discord_user_id = table.Column<string>(type: "text", nullable: false),
                    vrchat_user_id = table.Column<string>(type: "text", nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    started_from = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    checks = table.Column<int>(type: "integer", nullable: false),
                    last_check_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discord_link_code", x => x.discord_user_id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_discord_account_link_discord_active",
                table: "discord_account_link",
                column: "discord_user_id",
                unique: true,
                filter: "unlinked_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_discord_account_link_vrchat_active",
                table: "discord_account_link",
                column: "vrchat_user_id",
                unique: true,
                filter: "unlinked_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "discord_account_link");

            migrationBuilder.DropTable(
                name: "discord_link_code");

            migrationBuilder.DropColumn(
                name: "discord_eighteen_plus_role_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "discord_link_backup_channel_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "discord_link_prompt_new_members",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "discord_linked_role_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "discord_oauth_client_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "discord_oauth_client_secret_encrypted",
                table: "settings");
        }
    }
}
