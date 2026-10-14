using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SnapshotTheDiscordServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "event_summary_channel_id",
                table: "settings",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "event_summary_last_month",
                table: "settings",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "event_summary_on",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "event_summary_problem",
                table: "settings",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "calendar_interest",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurrence_starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    discord_interested = table.Column<int>(type: "integer", nullable: true),
                    discord_taken_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    vrchat_interested = table.Column<int>(type: "integer", nullable: true),
                    vrchat_taken_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_calendar_interest", x => new { x.event_id, x.occurrence_starts_at });
                    table.ForeignKey(
                        name: "fk_calendar_interest_calendar_event_event_id",
                        column: x => x.event_id,
                        principalTable: "calendar_event",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "calendar_interest");

            migrationBuilder.DropColumn(
                name: "event_summary_channel_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "event_summary_last_month",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "event_summary_on",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "event_summary_problem",
                table: "settings");
        }
    }
}
