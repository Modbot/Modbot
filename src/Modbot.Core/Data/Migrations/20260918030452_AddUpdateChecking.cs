using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUpdateChecking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "check_for_updates",
                table: "settings",
                type: "boolean",
                nullable: false,
                // On, like the C# default. A deployment that existed before this feature was built
                // should start being told about newer releases, not stay silently in the dark
                // because its settings row predates the column.
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "newest_release",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "newest_release_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "newest_release_image",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "newest_release_notes_url",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "newest_release_tag",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "update_check_problem",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "update_checked_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "check_for_updates",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "newest_release",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "newest_release_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "newest_release_image",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "newest_release_notes_url",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "newest_release_tag",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "update_check_problem",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "update_checked_at",
                table: "settings");
        }
    }
}
