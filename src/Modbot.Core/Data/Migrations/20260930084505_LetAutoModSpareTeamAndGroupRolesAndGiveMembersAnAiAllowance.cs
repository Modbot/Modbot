using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class LetAutoModSpareTeamAndGroupRolesAndGiveMembersAnAiAllowance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ai_member_monthly_money",
                table: "settings",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ai_member_monthly_tokens",
                table: "settings",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "exempt_group_roles",
                table: "automod_term_list",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<bool>(
                name: "exempt",
                table: "automod_flag",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "exempt_group_roles",
                table: "ai_topic",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.CreateTable(
                name: "ai_member_allowance",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    monthly_tokens = table.Column<long>(type: "bigint", nullable: true),
                    monthly_money = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_member_allowance", x => x.user_id);
                    table.ForeignKey(
                        name: "fk_ai_member_allowance_modbot_user_user_id",
                        column: x => x.user_id,
                        principalTable: "modbot_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_member_allowance");

            migrationBuilder.DropColumn(
                name: "ai_member_monthly_money",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "ai_member_monthly_tokens",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "exempt_group_roles",
                table: "automod_term_list");

            migrationBuilder.DropColumn(
                name: "exempt",
                table: "automod_flag");

            migrationBuilder.DropColumn(
                name: "exempt_group_roles",
                table: "ai_topic");
        }
    }
}
