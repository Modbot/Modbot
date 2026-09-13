using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncCursors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "audit_log_backfill_complete",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "audit_log_backfill_offset",
                table: "settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "audit_log_catch_up_offset",
                table: "settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "audit_log_polled_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "audit_log_synced_through",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "group_info_polled_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "group_info_snapshot",
                table: "settings",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "audit_log_backfill_complete",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "audit_log_backfill_offset",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "audit_log_catch_up_offset",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "audit_log_polled_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "audit_log_synced_through",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "group_info_polled_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "group_info_snapshot",
                table: "settings");
        }
    }
}
