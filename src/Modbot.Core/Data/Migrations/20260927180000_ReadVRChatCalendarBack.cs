using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReadVRChatCalendarBack : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "made_on_vrchat",
                table: "calendar_event",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "vrchat_close_instance_after_end_minutes",
                table: "calendar_event",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "vrchat_featured",
                table: "calendar_event",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "vrchat_guest_early_join_minutes",
                table: "calendar_event",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "vrchat_host_early_join_minutes",
                table: "calendar_event",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "vrchat_role_ids",
                table: "calendar_event",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "vrchat_uses_instance_overflow",
                table: "calendar_event",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "vrchat_updated_at",
                table: "calendar_event_place",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "made_on_vrchat", table: "calendar_event");
            migrationBuilder.DropColumn(name: "vrchat_close_instance_after_end_minutes", table: "calendar_event");
            migrationBuilder.DropColumn(name: "vrchat_featured", table: "calendar_event");
            migrationBuilder.DropColumn(name: "vrchat_guest_early_join_minutes", table: "calendar_event");
            migrationBuilder.DropColumn(name: "vrchat_host_early_join_minutes", table: "calendar_event");
            migrationBuilder.DropColumn(name: "vrchat_role_ids", table: "calendar_event");
            migrationBuilder.DropColumn(name: "vrchat_uses_instance_overflow", table: "calendar_event");
            migrationBuilder.DropColumn(name: "vrchat_updated_at", table: "calendar_event_place");
        }
    }
}
