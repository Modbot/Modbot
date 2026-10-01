using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// The switch for uploading a calendar picture to VRChat (calendar design §2). Off on every
    /// deployment, new or old: VRChat takes the upload only from an account with VRChat+, and a
    /// scripted upload is something an operator turns on, not finds on.
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
                defaultValue: false);
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
