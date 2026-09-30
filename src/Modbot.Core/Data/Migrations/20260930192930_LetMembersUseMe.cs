using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// Adds the "Members can use /me" switch (Discord /me design, 2026-09-30). Off for every
    /// deployment, new and existing: a command every member can run is the operator's choice.
    /// </summary>
    public partial class LetMembersUseMe : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "discord_me_command",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "discord_me_command",
                table: "settings");
        }
    }
}
