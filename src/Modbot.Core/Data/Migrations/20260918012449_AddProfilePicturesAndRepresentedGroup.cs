using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProfilePicturesAndRepresentedGroup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "banner_url",
                table: "vrchat_user",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "icon_url",
                table: "vrchat_user",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "represented_group_icon_url",
                table: "vrchat_user",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "represented_group_id",
                table: "vrchat_user",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "represented_group_name",
                table: "vrchat_user",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "banner_url",
                table: "vrchat_user");

            migrationBuilder.DropColumn(
                name: "icon_url",
                table: "vrchat_user");

            migrationBuilder.DropColumn(
                name: "represented_group_icon_url",
                table: "vrchat_user");

            migrationBuilder.DropColumn(
                name: "represented_group_id",
                table: "vrchat_user");

            migrationBuilder.DropColumn(
                name: "represented_group_name",
                table: "vrchat_user");
        }
    }
}
