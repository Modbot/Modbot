using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// The Settings topic "Posts" gains VRChat posts (posts design §3.6, §4.6). On to start, on old
    /// installs too, like Discord posts: nothing goes to the VRChat group unless a person ticks
    /// VRChat on a post. The VRChat choices of a post live in <c>post_destination.options</c>, which
    /// already exists.
    /// </summary>
    public partial class SendPostsToTheVRChatGroup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "vr_chat_posts_on",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "vr_chat_posts_on",
                table: "settings");
        }
    }
}
