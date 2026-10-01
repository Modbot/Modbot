using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class InviteOnlyPeopleWhoAsked : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "gets_event_invites",
                table: "modbot_user",
                type: "boolean",
                nullable: false,
                // On for the staff accounts that already exist, as for new ones: being invited to
                // your own event as its staff is not an invite nobody asked for.
                defaultValue: true);

            migrationBuilder.AddColumn<Guid>(
                name: "staff_user_id",
                table: "calendar_invite",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "event_invite_choice",
                columns: table => new
                {
                    discord_user_id = table.Column<string>(type: "text", nullable: false),
                    vrchat_user_id = table.Column<string>(type: "text", nullable: true),
                    wants = table.Column<bool>(type: "boolean", nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_invite_choice", x => x.discord_user_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_event_invite_choice_vrchat_user_id",
                table: "event_invite_choice",
                column: "vrchat_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "event_invite_choice");

            migrationBuilder.DropColumn(
                name: "gets_event_invites",
                table: "modbot_user");

            migrationBuilder.DropColumn(
                name: "staff_user_id",
                table: "calendar_invite");
        }
    }
}
