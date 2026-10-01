using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// The switch for uploading a calendar picture to VRChat (calendar design §2). On for every
    /// deployment, new or old, because the event form already offered the upload; an operator
    /// who does not want scripted uploads turns it off.
    /// </summary>
    public partial class AddVRChatPictureUploadsSwitch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "vr_chat_picture_uploads",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "vr_chat_picture_uploads",
                table: "settings");
        }
    }
}
