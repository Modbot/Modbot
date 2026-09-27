using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddServerPicturesAndBoosts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "banner_url",
                table: "discord_server",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "boost_count",
                table: "discord_server",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "boost_level",
                table: "discord_server",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "icon_url",
                table: "discord_server",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "banner_url",
                table: "discord_server");

            migrationBuilder.DropColumn(
                name: "boost_count",
                table: "discord_server");

            migrationBuilder.DropColumn(
                name: "boost_level",
                table: "discord_server");

            migrationBuilder.DropColumn(
                name: "icon_url",
                table: "discord_server");
        }
    }
}
