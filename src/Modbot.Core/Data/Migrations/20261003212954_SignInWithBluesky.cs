using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SignInWithBluesky : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "bluesky_oauth_key_encrypted",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bluesky_oauth_pending_encrypted",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "bluesky_oauth_signed_in",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bluesky_oauth_key_encrypted",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "bluesky_oauth_pending_encrypted",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "bluesky_oauth_signed_in",
                table: "settings");
        }
    }
}
