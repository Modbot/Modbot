using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class LinkProfileOptionsToRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "discord_role_id",
                table: "member_profile_option",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "member_profile",
                columns: table => new
                {
                    discord_user_id = table.Column<string>(type: "text", nullable: false),
                    findable = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_member_profile", x => x.discord_user_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "member_profile");

            migrationBuilder.DropColumn(
                name: "discord_role_id",
                table: "member_profile_option");
        }
    }
}
