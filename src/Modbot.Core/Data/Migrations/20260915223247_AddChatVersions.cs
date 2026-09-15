using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChatVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "cached_input_tokens",
                table: "ai_chat_message",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "input_tokens",
                table: "ai_chat_message",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "model",
                table: "ai_chat_message",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "output_tokens",
                table: "ai_chat_message",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "parent_id",
                table: "ai_chat_message",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "reported_cost",
                table: "ai_chat_message",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "stopped",
                table: "ai_chat_message",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "leaf_id",
                table: "ai_chat_conversation",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_ai_chat_message_conversation_id_parent_id",
                table: "ai_chat_message",
                columns: new[] { "conversation_id", "parent_id" });

            // Conversations written before versions existed are one straight line: each message
            // follows the one before it, and the last one is what the page shows.
            migrationBuilder.Sql("""
                UPDATE ai_chat_message AS m
                SET parent_id = ordered.before
                FROM (
                    SELECT id, lag(id) OVER (PARTITION BY conversation_id ORDER BY id) AS before
                    FROM ai_chat_message
                ) AS ordered
                WHERE ordered.id = m.id AND ordered.before IS NOT NULL;
                """);

            migrationBuilder.Sql("""
                UPDATE ai_chat_conversation AS c
                SET leaf_id = (SELECT max(m.id) FROM ai_chat_message AS m WHERE m.conversation_id = c.id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_ai_chat_message_conversation_id_parent_id",
                table: "ai_chat_message");

            migrationBuilder.DropColumn(
                name: "cached_input_tokens",
                table: "ai_chat_message");

            migrationBuilder.DropColumn(
                name: "input_tokens",
                table: "ai_chat_message");

            migrationBuilder.DropColumn(
                name: "model",
                table: "ai_chat_message");

            migrationBuilder.DropColumn(
                name: "output_tokens",
                table: "ai_chat_message");

            migrationBuilder.DropColumn(
                name: "parent_id",
                table: "ai_chat_message");

            migrationBuilder.DropColumn(
                name: "reported_cost",
                table: "ai_chat_message");

            migrationBuilder.DropColumn(
                name: "stopped",
                table: "ai_chat_message");

            migrationBuilder.DropColumn(
                name: "leaf_id",
                table: "ai_chat_conversation");
        }
    }
}
