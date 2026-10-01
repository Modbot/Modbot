using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// Lets a staff account prove its Discord account (accounts and access design §4.6): the
    /// username Discord gave, and when it was proven. Ids typed in before this stay as they are and
    /// count as unproven. Unique among proven ids only, because typed ones were never checked and
    /// two accounts may hold the same one.
    /// </summary>
    public partial class ProveStaffDiscordAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "discord_username",
                table: "modbot_user",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "discord_verified_at",
                table: "modbot_user",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_modbot_user_discord_user_id",
                table: "modbot_user",
                column: "discord_user_id",
                unique: true,
                filter: "discord_verified_at IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_modbot_user_discord_user_id",
                table: "modbot_user");

            migrationBuilder.DropColumn(
                name: "discord_username",
                table: "modbot_user");

            migrationBuilder.DropColumn(
                name: "discord_verified_at",
                table: "modbot_user");
        }
    }
}
