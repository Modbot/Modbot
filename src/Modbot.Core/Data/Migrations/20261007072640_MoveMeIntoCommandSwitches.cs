using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// Replaces the one "Members can use /me" column with <c>discord_commands</c>, a JSON object
    /// mapping a command's name to on or off (Discord commands design §3.8). The old value is copied
    /// before the column goes: a deployment that had <c>/me</c> on keeps it on, and one that had it
    /// off has nothing stored, which is the same thing because <c>/me</c> is off by default. Every
    /// other command has no stored choice, so it takes its default.
    /// </summary>
    public partial class MoveMeIntoCommandSwitches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "discord_commands",
                table: "settings",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.Sql(
                "UPDATE settings SET discord_commands = '{\"me\": true}'::jsonb WHERE discord_me_command;");

            migrationBuilder.DropColumn(
                name: "discord_me_command",
                table: "settings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "discord_me_command",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(
                "UPDATE settings SET discord_me_command = COALESCE((discord_commands ->> 'me')::boolean, false);");

            migrationBuilder.DropColumn(
                name: "discord_commands",
                table: "settings");
        }
    }
}
