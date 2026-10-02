using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// Calendar design §2 (2026-10-02): a repeat every N days, weeks or months and a number of times,
    /// and Featured as the form's own field. Featured was kept only as VRChat said it, for an event
    /// read from VRChat's calendar, so what it said is carried over and an event made in Modbot
    /// starts off, as it was sent until now.
    /// </summary>
    public partial class LetEventsRepeatEveryFewWeeksAndBeFeatured : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "featured",
                table: "calendar_event",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("UPDATE calendar_event SET featured = TRUE WHERE vrchat_featured IS TRUE;");

            migrationBuilder.DropColumn(
                name: "vrchat_featured",
                table: "calendar_event");

            migrationBuilder.AddColumn<int>(
                name: "repeat_every",
                table: "calendar_event",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "repeat_times",
                table: "calendar_event",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "vrchat_featured",
                table: "calendar_event",
                type: "boolean",
                nullable: true);

            migrationBuilder.Sql("UPDATE calendar_event SET vrchat_featured = TRUE WHERE featured;");

            migrationBuilder.DropColumn(
                name: "featured",
                table: "calendar_event");

            migrationBuilder.DropColumn(
                name: "repeat_every",
                table: "calendar_event");

            migrationBuilder.DropColumn(
                name: "repeat_times",
                table: "calendar_event");
        }
    }
}
