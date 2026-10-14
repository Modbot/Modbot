using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSupporterCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "given_discord_user_id",
                table: "supporter",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "supporter_code",
                columns: table => new
                {
                    discord_user_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    code = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_supporter_code", x => x.discord_user_id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_supporter_code_code",
                table: "supporter_code",
                column: "code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "supporter_code");

            migrationBuilder.DropColumn(
                name: "given_discord_user_id",
                table: "supporter");
        }
    }
}
