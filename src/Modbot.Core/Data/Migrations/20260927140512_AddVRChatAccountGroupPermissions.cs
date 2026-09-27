using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVRChatAccountGroupPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "vr_chat_account_permissions",
                table: "settings",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "vr_chat_account_role_ids",
                table: "settings",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "missing_group_permission",
                table: "calendar_event_place",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "vr_chat_account_permissions",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "vr_chat_account_role_ids",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "missing_group_permission",
                table: "calendar_event_place");
        }
    }
}
