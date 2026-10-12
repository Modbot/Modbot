using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// 2026-10-12: Discord asks for Create Events, not only Manage Events, to make a server event.
    /// One column: whether the bot holds it. New rows start as without it; existing rows copy
    /// Manage Events until the bot connects and reads the server again.
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

            // A server that holds Manage Events is taken to hold Create Events until the bot's
            // next read says otherwise, so an upgrade does not show a false "Missing Create Events".
            migrationBuilder.Sql("UPDATE discord_server SET bot_can_create_events = bot_can_manage_events;");
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
