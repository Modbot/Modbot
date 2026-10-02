using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// A Discord role's permissions and whether @everyone sees a channel, for the roles report and
    /// the quiet channels list (Discord tidy-up design). Null on every row until the bot reads the
    /// server again, which it does on its next sign-in or resume; nothing is filled in by hand.
    /// </summary>
    public partial class KeepRolePermissionsAndWhoSeesEachChannel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "permissions",
                table: "discord_role",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "everyone_can_view",
                table: "discord_channel",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "permissions",
                table: "discord_role");

            migrationBuilder.DropColumn(
                name: "everyone_can_view",
                table: "discord_channel");
        }
    }
}
