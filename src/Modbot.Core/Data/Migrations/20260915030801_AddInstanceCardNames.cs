using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInstanceCardNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "discord_instance_show_names",
                table: "settings",
                type: "boolean",
                nullable: false,
                // On for every existing deployment too: names show while a moderator is watching
                // unless an operator turns them off (M3 section 7.4.4).
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "discord_instance_show_names",
                table: "settings");
        }
    }
}
