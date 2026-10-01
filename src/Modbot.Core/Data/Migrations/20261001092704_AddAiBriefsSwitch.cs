using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// The switch for AI briefs on Settings → AI → Chat (AI chat design §14). Off on every
    /// deployment, new or old: a brief sends audit log entries to the AI provider, so an operator
    /// turns it on.
    /// </summary>
    public partial class AddAiBriefsSwitch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ai_briefs_enabled",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ai_briefs_enabled",
                table: "settings");
        }
    }
}
