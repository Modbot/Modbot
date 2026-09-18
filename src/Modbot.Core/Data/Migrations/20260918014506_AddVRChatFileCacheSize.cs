using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVRChatFileCacheSize : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 2 GB, the same number new deployments get. The column default matters here: 0 means
            // "cache nothing", so letting it stand would leave every existing deployment fetching
            // every picture from VRChat again on every screen.
            migrationBuilder.AddColumn<long>(
                name: "vr_chat_file_cache_bytes",
                table: "settings",
                type: "bigint",
                nullable: false,
                defaultValue: 2147483648L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "vr_chat_file_cache_bytes",
                table: "settings");
        }
    }
}
