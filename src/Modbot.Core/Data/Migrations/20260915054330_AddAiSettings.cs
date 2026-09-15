using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAiSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ai_api_key_encrypted",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ai_enabled",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ai_endpoint",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ai_model",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ai_provider",
                table: "settings",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ai_api_key_encrypted",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "ai_enabled",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "ai_endpoint",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "ai_model",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "ai_provider",
                table: "settings");
        }
    }
}
