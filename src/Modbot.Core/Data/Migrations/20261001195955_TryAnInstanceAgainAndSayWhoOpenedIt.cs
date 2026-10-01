using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class TryAnInstanceAgainAndSayWhoOpenedIt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "opened_by_user_id",
                table: "calendar_opening",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "try_again",
                table: "calendar_opening",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "opened_by_user_id",
                table: "calendar_opening");

            migrationBuilder.DropColumn(
                name: "try_again",
                table: "calendar_opening");
        }
    }
}
