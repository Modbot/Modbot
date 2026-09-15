using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVRChatSignInLimit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "vr_chat_last_signed_in_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "vr_chat_session_account",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "vr_chat_session_check_user_id",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "vr_chat_session_user_id",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "vr_chat_sign_in_wait_reason",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "vr_chat_sign_in_wait_until",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "vrchat_sign_in_attempt",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    operation = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vrchat_sign_in_attempt", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_vrchat_sign_in_attempt_at",
                table: "vrchat_sign_in_attempt",
                column: "at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "vrchat_sign_in_attempt");

            migrationBuilder.DropColumn(
                name: "vr_chat_last_signed_in_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "vr_chat_session_account",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "vr_chat_session_check_user_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "vr_chat_session_user_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "vr_chat_sign_in_wait_reason",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "vr_chat_sign_in_wait_until",
                table: "settings");
        }
    }
}
