using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscordRoleMenus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "discord_role_menu",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    channel_id = table.Column<string>(type: "text", nullable: false),
                    text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    style = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    single_choice = table.Column<bool>(type: "boolean", nullable: false),
                    message_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    posted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discord_role_menu", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "discord_role_menu_role",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    menu_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<string>(type: "text", nullable: false),
                    label = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    emoji = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discord_role_menu_role", x => x.id);
                    table.ForeignKey(
                        name: "fk_discord_role_menu_role_discord_role_menu_menu_id",
                        column: x => x.menu_id,
                        principalTable: "discord_role_menu",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_discord_role_menu_role_order",
                table: "discord_role_menu_role",
                columns: new[] { "menu_id", "position" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "discord_role_menu_role");

            migrationBuilder.DropTable(
                name: "discord_role_menu");
        }
    }
}
