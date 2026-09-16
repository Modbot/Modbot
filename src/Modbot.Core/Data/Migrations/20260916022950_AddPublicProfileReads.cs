using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPublicProfileReads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_user_read_at",
                table: "vrchat_user",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "profile_not_found_at",
                table: "vrchat_user",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "raw_public_profile",
                table: "vrchat_user",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "user_not_found_at",
                table: "vrchat_user",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "user_read_error",
                table: "vrchat_user",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "user_read_error_at",
                table: "vrchat_user",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_vrchat_user_last_user_read",
                table: "vrchat_user",
                column: "last_user_read_at");

            // Every 404 recorded before this release came from the one read there was, which is
            // the one the public profile has taken over. Carrying it across keeps a person who is
            // really gone marked as gone: without it, the first user read that also 404s would
            // find no matching mark and would clear the row's "missing" flag rather than confirm
            // it (research: vrchat-public-profile-findings.md §6).
            migrationBuilder.Sql(
                "UPDATE vrchat_user SET profile_not_found_at = not_found_at WHERE not_found_at IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_vrchat_user_last_user_read",
                table: "vrchat_user");

            migrationBuilder.DropColumn(
                name: "last_user_read_at",
                table: "vrchat_user");

            migrationBuilder.DropColumn(
                name: "profile_not_found_at",
                table: "vrchat_user");

            migrationBuilder.DropColumn(
                name: "raw_public_profile",
                table: "vrchat_user");

            migrationBuilder.DropColumn(
                name: "user_not_found_at",
                table: "vrchat_user");

            migrationBuilder.DropColumn(
                name: "user_read_error",
                table: "vrchat_user");

            migrationBuilder.DropColumn(
                name: "user_read_error_at",
                table: "vrchat_user");
        }
    }
}
