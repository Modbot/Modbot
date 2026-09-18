using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProxyVRChatImagesSwitch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "vr_chat_images_proxied",
                table: "settings",
                type: "boolean",
                nullable: false,
                // True, not EF's false: every deployment that already exists loads its pictures
                // through Modbot, and a default of false would take every face off every screen
                // the moment this migration ran.
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "vr_chat_images_proxied",
                table: "settings");
        }
    }
}
