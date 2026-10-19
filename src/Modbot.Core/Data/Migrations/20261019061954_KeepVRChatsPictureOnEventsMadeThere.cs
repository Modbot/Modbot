using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// 2026-10-19: VRChat refused the update of an event made on VRChat whose picture another user
    /// uploaded. Keeps the picture VRChat itself last said, so a write can tell a moderator's change
    /// from VRChat's own value, as vrchat_featured does for Featured.
    /// </summary>
    public partial class KeepVRChatsPictureOnEventsMadeThere : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "vrchat_picture_id",
                table: "calendar_event",
                type: "text",
                nullable: true);

            // An event made on VRChat has the picture VRChat gave it, unless a moderator changed it
            // since, which could not be told apart before this column: taken as VRChat's.
            migrationBuilder.Sql("UPDATE calendar_event SET vrchat_picture_id = vrchat_image_id WHERE made_on_vrchat;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "vrchat_picture_id",
                table: "calendar_event");
        }
    }
}
