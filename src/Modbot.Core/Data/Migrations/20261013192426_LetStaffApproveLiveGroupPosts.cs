using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class LetStaffApproveLiveGroupPosts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "live_post_auto",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "live_post_from",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "live_post_scope",
                table: "settings",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "off");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "live_post_auto",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "live_post_from",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "live_post_scope",
                table: "settings");
        }
    }
}
