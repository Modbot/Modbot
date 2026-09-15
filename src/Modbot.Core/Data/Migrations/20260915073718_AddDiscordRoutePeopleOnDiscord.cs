using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// Lets a route's person filters name Discord accounts as well as VRChat ones, so somebody who
    /// is only on Discord, or never linked, can be filtered (Discord event routes design §3.1).
    /// </summary>
    /// <remarks>
    /// Checked by hand: two added columns, and every existing route gets an empty list, which is
    /// "anyone" -- nothing an existing route sends changes.
    /// </remarks>
    public partial class AddDiscordRoutePeopleOnDiscord : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "actor_discord_ids",
                table: "discord_event_route",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "subject_discord_ids",
                table: "discord_event_route",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "actor_discord_ids",
                table: "discord_event_route");

            migrationBuilder.DropColumn(
                name: "subject_discord_ids",
                table: "discord_event_route");
        }
    }
}
