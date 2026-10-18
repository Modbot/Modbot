using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class GoogleEventColourAndBusy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "google_colour",
                table: "calendar_event",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "google_show_as",
                table: "calendar_event",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "free");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "google_colour",
                table: "calendar_event");

            migrationBuilder.DropColumn(
                name: "google_show_as",
                table: "calendar_event");
        }
    }
}
