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
            migrationBuilder.DropIndex(
                name: "ix_supporter_discord_user",
                table: "supporter");

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

            // One account belongs to one supporter per site from now on. Should two rows already share
            // an account, the newest payer keeps it and the others go back to Not matched for staff.
            migrationBuilder.Sql("""
                UPDATE supporter s SET discord_user_id = NULL
                WHERE s.discord_user_id IS NOT NULL
                  AND EXISTS (
                    SELECT 1 FROM supporter n
                    WHERE n.provider = s.provider
                      AND n.discord_user_id = s.discord_user_id
                      AND (n.last_paid_at, n.id) > (s.last_paid_at, s.id));
                """);

            migrationBuilder.CreateIndex(
                name: "ux_supporter_discord_user",
                table: "supporter",
                columns: new[] { "provider", "discord_user_id" },
                unique: true);

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

            migrationBuilder.DropIndex(
                name: "ux_supporter_discord_user",
                table: "supporter");

            migrationBuilder.DropColumn(
                name: "given_discord_user_id",
                table: "supporter");

            migrationBuilder.CreateIndex(
                name: "ix_supporter_discord_user",
                table: "supporter",
                column: "discord_user_id");
        }
    }
}
