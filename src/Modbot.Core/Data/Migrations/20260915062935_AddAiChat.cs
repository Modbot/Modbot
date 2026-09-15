using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// Chat's settings on the settings row, and the conversation tables (AI chat design §5, §6).
    /// </summary>
    /// <remarks>
    /// The defaults are hand-set to the entity's own starting values, so the existing settings row
    /// gets the limits a new one would rather than zero tool calls, zero tokens and no time at all.
    /// </remarks>
    public partial class AddAiChat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ai_chat_enabled",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ai_chat_instructions",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ai_chat_max_reply_tokens",
                table: "settings",
                type: "integer",
                nullable: false,
                defaultValue: 2000);

            migrationBuilder.AddColumn<int>(
                name: "ai_chat_max_tool_calls",
                table: "settings",
                type: "integer",
                nullable: false,
                defaultValue: 8);

            migrationBuilder.AddColumn<string>(
                name: "ai_chat_model",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ai_chat_time_limit_seconds",
                table: "settings",
                type: "integer",
                nullable: false,
                defaultValue: 120);

            migrationBuilder.AddColumn<string>(
                name: "ai_chat_tool_switches",
                table: "settings",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.CreateTable(
                name: "ai_chat_conversation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_chat_conversation", x => x.id);
                    table.ForeignKey(
                        name: "fk_ai_chat_conversation_modbot_user_user_id",
                        column: x => x.user_id,
                        principalTable: "modbot_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ai_chat_message",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    tool_calls = table.Column<string>(type: "jsonb", nullable: true),
                    tool_call_id = table.Column<string>(type: "text", nullable: true),
                    tool_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    mentioned = table.Column<string>(type: "jsonb", nullable: true),
                    worked = table.Column<bool>(type: "boolean", nullable: true),
                    duration_ms = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_chat_message", x => x.id);
                    table.ForeignKey(
                        name: "fk_ai_chat_message_ai_chat_conversation_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "ai_chat_conversation",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_chat_conversation_user_id_updated_at",
                table: "ai_chat_conversation",
                columns: new[] { "user_id", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_chat_message_conversation_id_id",
                table: "ai_chat_message",
                columns: new[] { "conversation_id", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_chat_message");

            migrationBuilder.DropTable(
                name: "ai_chat_conversation");

            migrationBuilder.DropColumn(
                name: "ai_chat_enabled",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "ai_chat_instructions",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "ai_chat_max_reply_tokens",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "ai_chat_max_tool_calls",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "ai_chat_model",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "ai_chat_time_limit_seconds",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "ai_chat_tool_switches",
                table: "settings");
        }
    }
}
