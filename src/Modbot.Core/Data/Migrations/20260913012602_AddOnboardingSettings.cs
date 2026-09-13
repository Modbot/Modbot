using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOnboardingSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "connection_checked_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "discord_bot_token_encrypted",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "discord_guild_id",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "smtp_from_address",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "smtp_host",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "smtp_password_encrypted",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "smtp_port",
                table: "settings",
                type: "integer",
                nullable: true);

            // True, not EF's bool default of false: a relay that needs TLS turned off is the
            // unusual one, and a row that predates this column should not silently become the
            // configuration nobody would have chosen.
            migrationBuilder.AddColumn<bool>(
                name: "smtp_use_tls",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "smtp_username",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "vr_chat_display_name",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "vr_chat_verified_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "connection_checked_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "discord_bot_token_encrypted",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "discord_guild_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "smtp_from_address",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "smtp_host",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "smtp_password_encrypted",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "smtp_port",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "smtp_use_tls",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "smtp_username",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "vr_chat_display_name",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "vr_chat_verified_at",
                table: "settings");
        }
    }
}
