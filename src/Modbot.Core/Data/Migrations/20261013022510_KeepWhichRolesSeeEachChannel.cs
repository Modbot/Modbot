using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// The roles each channel's own overwrites let see it or hide it from, for the channels list's
    /// "Seen by" filter (Discord tidy-up design). Null on every row until the bot reads the server
    /// again, which it does on its next sign-in or resume; nothing is filled in by hand.
    /// </summary>
    public partial class KeepWhichRolesSeeEachChannel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "view_overwrites",
                table: "discord_channel",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "view_overwrites",
                table: "discord_channel");
        }
    }
}
