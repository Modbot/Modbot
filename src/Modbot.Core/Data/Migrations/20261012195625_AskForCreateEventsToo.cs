using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// 2026-10-12: Discord asks for Create Events, not only Manage Events, to make a server event.
    /// One column: whether the bot holds it. Rows start as without it and are read again when the
    /// bot connects.
    /// </summary>
    public partial class AskForCreateEventsToo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "bot_can_create_events",
                table: "discord_server",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bot_can_create_events",
                table: "discord_server");
        }
    }
}
