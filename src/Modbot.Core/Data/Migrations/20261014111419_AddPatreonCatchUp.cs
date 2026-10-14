using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPatreonCatchUp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "discord_lookup_at",
                table: "supporter",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "patreon_member_id",
                table: "supporter",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "patreon_access_token_encrypted",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "patreon_campaign_id",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "patreon_catch_up_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "patreon_catch_up_due_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "patreon_catch_up_problem",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "patreon_client_id_encrypted",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "patreon_client_secret_encrypted",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "patreon_refresh_token_encrypted",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "patreon_stopped_until",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "patreon_token_expires_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "discord_lookup_at",
                table: "supporter");

            migrationBuilder.DropColumn(
                name: "patreon_member_id",
                table: "supporter");

            migrationBuilder.DropColumn(
                name: "patreon_access_token_encrypted",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "patreon_campaign_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "patreon_catch_up_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "patreon_catch_up_due_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "patreon_catch_up_problem",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "patreon_client_id_encrypted",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "patreon_client_secret_encrypted",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "patreon_refresh_token_encrypted",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "patreon_stopped_until",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "patreon_token_expires_at",
                table: "settings");
        }
    }
}
